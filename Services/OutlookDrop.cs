using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
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
        /// Reads a normal file drop straight from the source. Needed for Chromium-based apps such as the new Outlook,
        /// which only write the file once the drop target announces an "async operation" (otherwise they reply
        /// DV_E_FORMATETC). Returns the file names and their bytes, read before the source cleans up.
        /// </summary>
        public static async Task<List<(string Name, byte[] Bytes)>> ReadFileDropRawAsync(
            System.Windows.IDataObject data, Action<string>? status)
        {
            ComDataObject com = Unwrap(data) ?? throw new Exception("could not reach the raw drag data");

            IDataObjectAsyncCapability? async = null;
            bool started = false;
            string asyncInfo = "async: not offered";
            try
            {
                async = com as IDataObjectAsyncCapability;
                if (async != null)
                {
                    async.GetAsyncMode(out bool isAsync);
                    asyncInfo = "async: offered, mode " + (isAsync ? "on" : "off");
                    if (isAsync)
                    {
                        async.StartOperation(IntPtr.Zero);
                        started = true;
                        asyncInfo += ", started";
                    }
                }
            }
            catch (Exception ex)
            {
                asyncInfo += ", error " + ex.Message;
            }

            if (!started)
            {
                // Ordinary source: the file is available right now, during the drop.
                try
                {
                    var files = ReadHDrop(com);
                    if (files.Count > 0) return files;
                    throw new Exception("file list was empty or files did not exist [" + asyncInfo + "]");
                }
                catch (COMException ex)
                {
                    throw new Exception(ex.Message + " [" + asyncInfo + "]");
                }
            }

            // Async source (new Outlook, Chrome, Edge): do what File Explorer does. Let the drop finish, then
            // collect the file on a separate background (MTA) thread, which may block while the source prepares it.
            Guid iid = IID_IDataObject;
            Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref iid, com, out IntPtr stream));
            status?.Invoke("Getting the email from Outlook...");

            int hr = 0;
            try
            {
                return await RunOnMtaThread(() => CollectInBackground(stream, asyncInfo));
            }
            catch (Exception ex)
            {
                hr = ex.HResult == 0 ? DV_E_FORMATETC : ex.HResult;
                throw;
            }
            finally
            {
                try { async!.EndOperation(hr, IntPtr.Zero, 1 /* DROPEFFECT_COPY */); } catch { }
            }
        }

        private static List<(string Name, byte[] Bytes)> CollectInBackground(IntPtr stream, string asyncInfo)
        {
            CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            object? obj = null;
            try
            {
                Guid iid = IID_IDataObject;
                Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref iid, out obj));
                var com = (ComDataObject)obj;

                DateTime begin = DateTime.Now, deadline = begin.AddSeconds(60);
                int attempts = 0;
                string lastError = "";
                while (true)
                {
                    attempts++;
                    try
                    {
                        var files = ReadHDrop(com); // may block while the source writes the file
                        if (files.Count > 0) return files;
                        lastError = "file list was empty or files did not exist";
                    }
                    catch (COMException ex)
                    {
                        lastError = ex.Message;
                    }

                    if (DateTime.Now > deadline)
                        throw new Exception($"{lastError} [{asyncInfo}; background thread; {attempts} attempt(s) over " +
                                            $"{(DateTime.Now - begin).TotalSeconds:0} s]");
                    System.Threading.Thread.Sleep(500);
                }
            }
            finally
            {
                if (obj != null) Marshal.ReleaseComObject(obj);
                CoUninitialize();
            }
        }

        private static Task<T> RunOnMtaThread<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            var thread = new System.Threading.Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(System.Threading.ApartmentState.MTA);
            thread.Start();
            return tcs.Task;
        }

        /// <summary>
        /// Chromium apps attach "web custom data": a list of (type, value) pairs. Returns them for fallback and diagnosis.
        /// </summary>
        public static List<(string Type, string Value)> ReadChromiumCustomData(System.Windows.IDataObject data)
        {
            var list = new List<(string, string)>();
            byte[]? buf = null;
            const string format = "Chromium Web Custom MIME Data Format";
            try { buf = (data.GetData(format) as MemoryStream)?.ToArray(); } catch { }
            if (buf == null)
            {
                var com = Unwrap(data);
                if (com != null) buf = GetHGlobal(com, (short)DataFormats.GetDataFormat(format).Id, -1);
            }
            if (buf == null || buf.Length < 8) return list;

            // Chromium Pickle: uint32 payload size, uint32 count, then per entry two UTF-16 strings
            // (int32 length in chars, the characters, padded to 4 bytes).
            int pos = 4;
            int count = BitConverter.ToInt32(buf, pos); pos += 4;
            for (int i = 0; i < count && i < 50; i++)
            {
                string? type = ReadString16(buf, ref pos);
                string? value = ReadString16(buf, ref pos);
                if (type == null || value == null) break;
                list.Add((type, value));
            }
            return list;
        }

        private static string? ReadString16(byte[] buf, ref int pos)
        {
            if (pos + 4 > buf.Length) return null;
            int chars = BitConverter.ToInt32(buf, pos); pos += 4;
            int bytes = chars * 2;
            if (chars < 0 || pos + bytes > buf.Length) return null;
            string s = Encoding.Unicode.GetString(buf, pos, bytes);
            pos += (bytes + 3) & ~3;
            return s;
        }

        private static List<(string Name, byte[] Bytes)> ReadHDrop(ComDataObject com)
        {
            var result = new List<(string, byte[])>();
            var format = new FORMATETC
            {
                cfFormat = CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = IntPtr.Zero, tymed = TYMED.TYMED_HGLOBAL
            };
            com.GetData(ref format, out STGMEDIUM medium);
            try
            {
                foreach (string path in DropPaths(medium.unionmember))
                {
                    if (File.Exists(path)) result.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
                }
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
            return result;
        }

        private static List<string> DropPaths(IntPtr hDrop)
        {
            var paths = new List<string>();
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                DragQueryFile(hDrop, i, sb, sb.Capacity);
                paths.Add(sb.ToString());
            }
            return paths;
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

        private const short CF_HDROP = 15;
        private const uint COINIT_MULTITHREADED = 0x0;
        private static readonly Guid IID_IDataObject = new Guid("0000010e-0000-0000-C000-000000000046");

        [DllImport("ole32.dll")]
        private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnk, out IntPtr ppStm);

        [DllImport("ole32.dll")]
        private static extern int CoGetInterfaceAndReleaseStream(IntPtr pStm, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
        private const int DV_E_FORMATETC = unchecked((int)0x80040064);
        private const uint STGM_CREATE = 0x00001000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

        [ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataObjectAsyncCapability
        {
            void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool fDoOpAsync);
            void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool pfIsOpAsync);
            void StartOperation(IntPtr pbcReserved);
            void InOperation([MarshalAs(UnmanagedType.Bool)] out bool pfInAsyncOp);
            void EndOperation(int hResult, IntPtr pbcReserved, uint dwEffects);
        }
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
