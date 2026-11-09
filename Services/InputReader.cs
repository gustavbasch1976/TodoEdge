using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace TodoEdge
{
    /// <summary>Something to send to Claude: a screenshot, a PDF, or text (typed, dictated, an email, a document).</summary>
    public sealed class CaptureInput
    {
        public string Kind { get; set; } = "text"; // screenshot, image, pdf, email, document, text, dictation
        public byte[]? Png { get; set; }
        public byte[]? Pdf { get; set; }
        public string? Text { get; set; }
        public string? SourceName { get; set; }
        public byte[]? OriginalBytes { get; set; }
        public string? OriginalExtension { get; set; }

        public string BusyMessage => Kind switch
        {
            "screenshot" => "Claude is reading your screenshot...",
            "image" => "Claude is reading the image...",
            "pdf" => "Claude is reading " + (SourceName ?? "the PDF") + "...",
            "email" => "Claude is reading the email...",
            "document" => "Claude is reading " + (SourceName ?? "the document") + "...",
            "dictation" => "Claude is sorting what you said...",
            _ => "Claude is reading your text..."
        };
    }

    /// <summary>Turns dropped or pasted files into something Claude can read.</summary>
    internal static class InputReader
    {
        private const int MaxText = 30000;
        private const int MaxPdfBytes = 30 * 1024 * 1024;

        public static string SupportedList => "emails (.msg, .eml), PDFs, Word (.docx), images and text files";

        /// <summary>Reads a drag-and-drop or clipboard data object. Must run before the drop handler returns.</summary>
        public static CaptureInput? FromData(IDataObject data)
        {
            var problems = new List<string>();

            bool hasFiles = false;
            try { hasFiles = data.GetDataPresent(DataFormats.FileDrop); } catch { }
            if (hasFiles)
            {
                string[]? files = null;
                try { files = data.GetData(DataFormats.FileDrop) as string[]; } catch { }

                if (files != null && files.Length > 0)
                {
                    foreach (string f in files)
                    {
                        var input = FromFile(f);
                        if (input != null) return input;
                    }
                    throw new Exception("Can't read " + string.Join(", ", files.Select(Path.GetFileName)) + ".\n\nDrop " + SupportedList + ".");
                }

                // Chromium-based apps (new Outlook, browsers) need the file requested a different way.
                try
                {
                    var dropped = OutlookDrop.ReadFileDropRaw(data);
                    foreach (var (name, bytes) in dropped)
                    {
                        var input = FromBytes(name, bytes);
                        if (input != null) return input;
                    }
                    if (dropped.Count > 0)
                        throw new Exception("Can't read " + string.Join(", ", dropped.Select(d => d.Name)) + ".\n\nDrop " + SupportedList + ".");
                    problems.Add("Files: the app offered a file but sent none");
                }
                catch (Exception ex) when (!ex.Message.StartsWith("Can't read")) { problems.Add("Files: " + ex.Message); }
            }

            bool hasVirtual = false;
            try { hasVirtual = OutlookDrop.HasVirtualFiles(data); } catch { }
            if (hasVirtual)
            {
                try
                {
                    foreach (var (name, bytes) in OutlookDrop.Read(data))
                    {
                        var input = FromBytes(name, bytes);
                        if (input != null) return input;
                    }
                    problems.Add("Dragged item is not a supported type");
                }
                catch (Exception ex) { problems.Add("Dragged email/file: " + ex.Message); }

                // Outlook's fallback text is only a header line, so don't silently use it.
                throw new Exception("Could not read the dragged email.\n\n" + string.Join("\n", problems) +
                                    "\n\nFormats offered: " + Formats(data) +
                                    "\n\nWorkaround: drag the email to your desktop first, then drop that file here.");
            }

            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return new CaptureInput { Kind = "image", Png = ms.ToArray() };
                }
            }
            catch (Exception ex) { problems.Add("Image: " + ex.Message); }

            try
            {
                if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
                    && !string.IsNullOrWhiteSpace(text))
                    return new CaptureInput { Kind = "text", Text = Trim(text) };
            }
            catch (Exception ex) { problems.Add("Text: " + ex.Message); }

            if (problems.Count > 0)
                throw new Exception("Could not read what was dropped.\n\n" + string.Join("\n", problems) + "\n\nFormats offered: " + Formats(data));
            return null;
        }

        private static string Formats(IDataObject data)
        {
            try { return string.Join(", ", data.GetFormats(false)); }
            catch { return "unknown"; }
        }

        public static CaptureInput? FromFile(string path)
        {
            if (!File.Exists(path)) return null;
            if (!IsSupported(path)) return null;
            return FromBytes(Path.GetFileName(path), File.ReadAllBytes(path));
        }

        public static bool IsSupported(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ImageUtil.IsImageFile(name) || ImageUtil.IsTextFile(name) ||
                   ext == ".pdf" || ext == ".msg" || ext == ".eml" || ext == ".docx";
        }

        public static CaptureInput? FromBytes(string name, byte[] bytes)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            try
            {
                if (ImageUtil.IsImageFile(name))
                {
                    using var ms = new MemoryStream(bytes);
                    using var img = System.Drawing.Image.FromStream(ms);
                    return new CaptureInput { Kind = "image", Png = ImageUtil.ToPng(img), SourceName = name };
                }

                switch (ext)
                {
                    case ".pdf":
                        if (bytes.Length > MaxPdfBytes)
                            throw new Exception("That PDF is over 30 MB. Split it or send the pages that matter.");
                        return new CaptureInput { Kind = "pdf", Pdf = bytes, SourceName = name, OriginalBytes = bytes, OriginalExtension = ".pdf" };

                    case ".msg":
                        return new CaptureInput { Kind = "email", Text = Trim(EmailReader.FromMsg(bytes)), SourceName = name, OriginalBytes = bytes, OriginalExtension = ".msg" };

                    case ".eml":
                        return new CaptureInput { Kind = "email", Text = Trim(EmailReader.FromEml(bytes)), SourceName = name, OriginalBytes = bytes, OriginalExtension = ".eml" };

                    case ".docx":
                        return new CaptureInput { Kind = "document", Text = Trim(DocxText(bytes)), SourceName = name, OriginalBytes = bytes, OriginalExtension = ".docx" };

                    case ".txt":
                    case ".md":
                    case ".csv":
                        return new CaptureInput { Kind = "document", Text = Trim(Encoding.UTF8.GetString(bytes)), SourceName = name, OriginalBytes = bytes, OriginalExtension = ext };
                }
            }
            catch (Exception ex) when (ex.Message.StartsWith("That PDF") == false)
            {
                throw new Exception($"Could not read {name}: {ex.Message}");
            }
            return null;
        }

        /// <summary>Plain text from a Word file, without needing Word installed.</summary>
        private static string DocxText(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new Exception("not a Word document");
            using var reader = new StreamReader(entry.Open());
            string xml = reader.ReadToEnd();
            xml = xml.Replace("</w:p>", "\n").Replace("<w:tab/>", "\t").Replace("<w:br/>", "\n");
            string text = Regex.Replace(xml, "<[^>]+>", "");
            return WebUtility.HtmlDecode(text);
        }

        public static string HtmlToText(string html)
        {
            string s = Regex.Replace(html, @"<(style|script|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</tr>|</li>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t ]+", " ");
            s = Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n");
            return s.Trim();
        }

        private static string Trim(string text)
        {
            text = text.Trim();
            return text.Length > MaxText ? text.Substring(0, MaxText) : text;
        }
    }

    internal static class EmailReader
    {
        public static string FromMsg(byte[] bytes)
        {
            // MsgReader works from a file, so write a temporary copy.
            string tmp = Path.Combine(Path.GetTempPath(), "todoedge-" + Guid.NewGuid().ToString("N") + ".msg");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var msg = new MsgReader.Outlook.Storage.Message(tmp);
                string body = !string.IsNullOrWhiteSpace(msg.BodyText)
                    ? msg.BodyText
                    : InputReader.HtmlToText(msg.BodyHtml ?? "");
                string from = msg.Sender == null ? "" : $"{msg.Sender.DisplayName} <{msg.Sender.Email}>";
                string sent = msg.SentOn.HasValue ? msg.SentOn.Value.ToString("dd/MM/yyyy HH:mm") : "";
                string to = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false);
                string cc = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.Cc, false, false);
                return Format(from, to, cc, sent, msg.Subject, body);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        public static string FromEml(byte[] bytes)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "todoedge-" + Guid.NewGuid().ToString("N") + ".eml");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                var eml = MsgReader.Mime.Message.Load(new FileInfo(tmp));
                string body = "";
                if (eml.TextBody != null) body = Encoding.UTF8.GetString(eml.TextBody.Body);
                else if (eml.HtmlBody != null) body = InputReader.HtmlToText(Encoding.UTF8.GetString(eml.HtmlBody.Body));

                var h = eml.Headers;
                string from = h?.From == null ? "" : $"{h.From.DisplayName} <{h.From.Address}>";
                string to = h?.To == null ? "" : string.Join("; ", h.To.Select(r => r.DisplayName + " <" + r.Address + ">"));
                string cc = h?.Cc == null ? "" : string.Join("; ", h.Cc.Select(r => r.DisplayName + " <" + r.Address + ">"));
                string sent = h == null ? "" : h.DateSent.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                return Format(from, to, cc, sent, h?.Subject, body);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static string Format(string from, string to, string cc, string sent, string? subject, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine("From: " + from);
            if (to.Length > 0) sb.AppendLine("To: " + to);
            if (cc.Length > 0) sb.AppendLine("Cc: " + cc);
            if (sent.Length > 0) sb.AppendLine("Sent: " + sent);
            sb.AppendLine("Subject: " + subject);
            sb.AppendLine();
            sb.Append(body);
            return sb.ToString();
        }
    }
}
