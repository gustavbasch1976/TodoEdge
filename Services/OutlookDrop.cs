using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComFILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace TodoEdge
{
    /// <summary>
    /// Reads "virtual files" from a drag and drop, which is how Outlook (and some other apps) hand over an
    /// email or attachment: there is no file on disk, only a file name list and the file contents in memory.
    /// </summary>
    internal static class OutlookDrop
    {
        public static bool HasVirtualFiles(System.Windows.IDataObject data) =>
            data.GetDataPresent("FileGroupDescriptorW") || data.GetDataPresent("FileGroupDescriptor");

        public static List<(string Name, byte[] Bytes)> Read(System.Windows.IDataObject data)
        {
            var result = new List<(string, byte[])>();
            ComDataObject? com = Unwrap(data);
            if (com == null) throw new Exception("could not reach the raw drag data");

            List<string> names = ReadNames(com, data);
            if (names.Count == 0) throw new Exception("the dragged item had no file name list");

            short cf = (short)DataFormats.GetDataFormat("FileContents").Id;
            var errors = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                byte[]? bytes = ReadContents(com, cf, i, names.Count, errors);
                if (bytes != null && bytes.Length > 0) result.Add((names[i], bytes));
            }
            if (result.Count == 0)
                throw new Exception("could not read the contents of " + string.Join(", ", names) +
                                    (errors.Count > 0 ? " (" + string.Join("; ", errors) + ")" : ""));
            return result;
        }

        /// <summary>
        /// WPF wraps the drag source's COM data object. Its own wrapper rejects per-file requests, so dig out the
        /// original COM object and talk to it directly.
        /// </summary>
        private static ComDataObject? Unwrap(object obj, int depth = 0)
        {
            if (depth > 4) return null;
            if (Marshal.IsComObject(obj) && obj is ComDataObject direct) return direct;

            foreach (var field in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string)) continue;
                object? value;
                try { value = field.GetValue(obj); } catch { continue; }
                if (value == null || ReferenceEquals(value, obj)) continue;

                if (Marshal.IsComObject(value) && value is ComDataObject com) return com;
                if (value is System.Windows.IDataObject || value.GetType().Name.Contains("Ole"))
                {
                    var inner = Unwrap(value, depth + 1);
                    if (inner != null) return inner;
                }
            }
            return depth == 0 ? obj as ComDataObject : null;
        }

        private static List<string> ReadNames(ComDataObject com, System.Windows.IDataObject data)
        {
            foreach (bool unicode in new[] { true, false })
            {
                string formatName = unicode ? "FileGroupDescriptorW" : "FileGroupDescriptor";
                byte[]? buf = GetHGlobal(com, (short)DataFormats.GetDataFormat(formatName).Id, -1);
                if (buf == null)
                {
                    try { buf = (data.GetData(formatName) as MemoryStream)?.ToArray(); } catch { }
                }
                if (buf != null && buf.Length >= 4) return ParseNames(buf, unicode);
            }
            return new List<string>();
        }

        private static List<string> ParseNames(byte[] buf, bool unicode)
        {
            var names = new List<string>();
            int count = BitConverter.ToInt32(buf, 0);
            const int nameOffset = 72; // FILEDESCRIPTOR fields before the name
            int size = unicode ? 72 + 520 : 72 + 260;
            for (int i = 0; i < count; i++)
            {
                int start = 4 + i * size + nameOffset;
                if (start >= buf.Length) break;
                int len = Math.Min(unicode ? 520 : 260, buf.Length - start);
                string name = unicode ? Encoding.Unicode.GetString(buf, start, len) : Encoding.Default.GetString(buf, start, len);
                int nul = name.IndexOf('\0');
                names.Add(nul >= 0 ? name.Substring(0, nul) : name);
            }
            return names;
        }

        private static byte[]? GetHGlobal(ComDataObject com, short cf, int index)
        {
            var format = new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, ptd = IntPtr.Zero, tymed = TYMED.TYMED_HGLOBAL };
            try
            {
                com.GetData(ref format, out STGMEDIUM medium);
                try { return medium.tymed == TYMED.TYMED_HGLOBAL ? FromHGlobal(medium.unionmember) : null; }
                finally { ReleaseStgMedium(ref medium); }
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ReadContents(ComDataObject com, short cf, int index, int count, List<string> errors)
        {
            // Different apps accept different requests, so try the combinations in turn.
            var attempts = new List<(int lindex, TYMED tymed)>
            {
                (index, TYMED.TYMED_ISTORAGE | TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL),
                (index, TYMED.TYMED_ISTORAGE),
                (index, TYMED.TYMED_ISTREAM),
                (index, TYMED.TYMED_HGLOBAL),
            };
            if (count == 1)
            {
                attempts.Add((-1, TYMED.TYMED_ISTORAGE | TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL));
                attempts.Add((-1, TYMED.TYMED_HGLOBAL));
            }

            foreach (var (lindex, tymed) in attempts)
            {
                var format = new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = lindex, ptd = IntPtr.Zero, tymed = tymed };
                STGMEDIUM medium;
                try
                {
                    com.GetData(ref format, out medium);
                }
                catch (Exception ex)
                {
                    errors.Add($"{tymed}/{lindex}: {ex.Message}");
                    continue;
                }

                try
                {
                    switch (medium.tymed)
                    {
                        case TYMED.TYMED_HGLOBAL:
                            return FromHGlobal(medium.unionmember);
                        case TYMED.TYMED_ISTREAM:
                            return FromStream((ComStream)Marshal.GetObjectForIUnknown(medium.unionmember));
                        case TYMED.TYMED_ISTORAGE:
                            return FromStorage((IStorage)Marshal.GetObjectForIUnknown(medium.unionmember));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"reading {medium.tymed}: {ex.Message}");
                }
                finally
                {
                    ReleaseStgMedium(ref medium);
                }
            }
            return null;
        }

        private static byte[] FromHGlobal(IntPtr hGlobal)
        {
            int size = (int)GlobalSize(hGlobal);
            IntPtr p = GlobalLock(hGlobal);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(p, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
        }

        private static byte[] FromStream(ComStream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPtr);
                    int read = Marshal.ReadInt32(readPtr);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(readPtr);
            }
            return output.ToArray();
        }

        /// <summary>Outlook gives emails as an OLE storage; copy it into memory to get the .msg file bytes.</summary>
        private static byte[] FromStorage(IStorage source)
        {
            Marshal.ThrowExceptionForHR(CreateILockBytesOnHGlobal(IntPtr.Zero, true, out ILockBytes lockBytes));
            Marshal.ThrowExceptionForHR(StgCreateDocfileOnILockBytes(lockBytes,
                STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, out IStorage dest));
            try
            {
                source.CopyTo(0, null, IntPtr.Zero, dest);
                lockBytes.Flush();
                dest.Commit(0);

                lockBytes.Stat(out STATSTG stat, 1);
                var bytes = new byte[stat.cbSize];
                lockBytes.ReadAt(0, bytes, bytes.Length, out _);
                return bytes;
            }
            finally
            {
                Marshal.ReleaseComObject(dest);
                Marshal.ReleaseComObject(lockBytes);
                Marshal.ReleaseComObject(source);
            }
        }

        // ---------- interop ----------

        private const uint STGM_CREATE = 0x00001000;
        private const uint STGM_READWRITE = 0x00000002;
        private const uint STGM_SHARE_EXCLUSIVE = 0x00000010;

        [DllImport("ole32.dll")]
        private static extern int CreateILockBytesOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, out ILockBytes ppLkbyt);

        [DllImport("ole32.dll")]
        private static extern int StgCreateDocfileOnILockBytes(ILockBytes plkbyt, uint grfMode, uint reserved, out IStorage ppstgOpen);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStorage
        {
            void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, int grfMode, int reserved1, int reserved2, out ComStream ppstm);
            void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr reserved1, int grfMode, int reserved2, out ComStream ppstm);
            void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, int grfMode, int reserved1, int reserved2, out IStorage ppstg);
            void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr pstgPriority, int grfMode, IntPtr snbExclude, int reserved, out IStorage ppstg);
            void CopyTo(int ciidExclude, [MarshalAs(UnmanagedType.LPArray)] Guid[]? rgiidExclude, IntPtr snbExclude, IStorage pstgDest);
            void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IStorage pstgDest, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName, int grfFlags);
            void Commit(int grfCommitFlags);
            void Revert();
            void EnumElements(int reserved1, IntPtr reserved2, int reserved3, out IntPtr ppenum);
            void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsName);
            void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsOldName, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName);
            void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, ref ComFILETIME pctime, ref ComFILETIME patime, ref ComFILETIME pmtime);
            void SetClass(ref Guid clsid);
            void SetStateBits(int grfStateBits, int grfMask);
            void Stat(out STATSTG pstatstg, int grfStatFlag);
        }

        [ComImport, Guid("0000000A-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILockBytes
        {
            void ReadAt(long ulOffset, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pv, int cb, out int pcbRead);
            void WriteAt(long ulOffset, IntPtr pv, int cb, out int pcbWritten);
            void Flush();
            void SetSize(long cb);
            void LockRegion(long libOffset, long cb, int dwLockType);
            void UnlockRegion(long libOffset, long cb, int dwLockType);
            void Stat(out STATSTG pstatstg, int grfStatFlag);
        }
    }
}
