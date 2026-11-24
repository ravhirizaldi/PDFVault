// Build the Windows executable with build-desktop.sh.
#if WPF_DESKTOP
#nullable enable annotations
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
#if !WPF_DESKTOP
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
#endif

namespace PdfVaultDesktop
{
    internal static class Vault
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("LTXBUNDLE1");
        const int SaltLen = 16, IvLen = 16, MacLen = 32, Chunk = 1 << 20;
        static readonly int TrailerLen = Magic.Length + 4 + 8 + 4 + SaltLen + IvLen + MacLen;
        const int Iterations = 600000;
        // shortcut: the ZIP stays in RAM; use authenticated random-access storage if larger vaults are needed.
        const int MaxArchive = 512 * Chunk;

        static byte[] Derive(string pass, byte[] salt, int version)
        {
            using (var pbkdf = new Rfc2898DeriveBytes(pass, salt, Iterations,
                version == 1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256))
                return pbkdf.GetBytes(64); // 32 encryption + 32 MAC
        }

#if WPF_DESKTOP
        public static void Hide(string? pdfSource, bool generate, IList<string> images,
#else
        public static void Hide(string pdfSource, bool generate, IList<string> images,
#endif
                                string password, string output, Action<string> log,
                                CancellationToken cancel = default(CancellationToken), Action<int> progress = null)
        {
            cancel.ThrowIfCancellationRequested();
            output = Path.GetFullPath(output);
            if (images.Concat(new[] { pdfSource }).Any(p => p != null &&
                string.Equals(Path.GetFullPath(p), output, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose an output different from the source files.");
            WriteVault(output, password, images, new ZipArchiveEntry[0], dst =>
            {
                if (generate)
                {
                    byte[] page = MiniPdf.Create();
                    dst.Write(page, 0, page.Length);
                }
                else if (pdfSource != null)
                {
                    log("Copying source PDF…");
                    using (var src = File.OpenRead(pdfSource)) Copy(src, dst, cancel, Percent(progress, src.Length));
                }
            }, log, cancel, progress, null);
        }

        static void WriteVault(string output, string password, IList<string> images, ZipArchiveEntry[] retained,
            Action<Stream> writePrefix, Action<string> log, CancellationToken cancel, Action<int> progress, Action beforeReplace)
        {
            string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                log("Deriving key...");
                var salt = RandomBytes(SaltLen);
                var iv = RandomBytes(IvLen);
                var keys = Derive(password, salt, 2);
                using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var aes = Aes.Create())
                using (var hmac = new HMACSHA256(keys.Skip(32).ToArray()))
                {
                    writePrefix(dst);
                    long start = dst.Position;
                    log("Packing and encrypting images...");
                    long total = images.Sum(path => new FileInfo(path).Length) + retained.Sum(entry => entry.Length), packed = 0;
                    var packing = Percent(progress, total);
                    using (var transform = aes.CreateEncryptor(keys.Take(32).ToArray(), iv))
                    using (var encrypted = new CryptoStream(dst, transform, CryptoStreamMode.Write, true))
                    using (var zip = new ZipArchive(encrypted, ZipArchiveMode.Create, true))
                    {
                        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string Unique(string name)
                        {
                            string original = name;
                            for (int i = 2; !used.Add(name); i++)
                                name = Path.GetFileNameWithoutExtension(original) + "_" + i + Path.GetExtension(original);
                            return name;
                        }
                        foreach (var item in retained)
                        {
                            using (var input = item.Open())
                            using (var entry = zip.CreateEntry(Unique(item.FullName), CompressionLevel.Fastest).Open())
                                Copy(input, entry, cancel, done => packing(packed + done));
                            packed += item.Length;
                            if (dst.Position - start > MaxArchive) throw TooLarge();
                        }
                        foreach (var img in images)
                        {
                            using (var input = File.OpenRead(img))
                            using (var entry = zip.CreateEntry(Unique(Path.GetFileName(img)), CompressionLevel.Fastest).Open())
                            {
                                Copy(input, entry, cancel, done => packing(packed + done));
                                packed += input.Position;
                            }
                            if (dst.Position - start > MaxArchive) throw TooLarge();
                        }
                    }
                    long end = dst.Position;
                    long region = end - start;
                    if (region > MaxArchive) throw TooLarge();
                    byte[] header;
                    using (var ms = new MemoryStream())
                    using (var bw = new BinaryWriter(ms))
                    {
                        bw.Write(Magic);
                        bw.Write(2);
                        bw.Write(region);
                        bw.Write(Chunk);
                        bw.Write(salt);
                        bw.Write(iv);
                        header = ms.ToArray();
                    }
                    dst.Position = start;
                    log("Authenticating encrypted bundle…");
                    Hash(hmac, dst, region, header, header.Length, cancel, progress);
                    dst.Position = end;
                    dst.Write(header, 0, header.Length);
                    dst.Write(hmac.Hash, 0, MacLen);
                }
                cancel.ThrowIfCancellationRequested();
                if (beforeReplace != null) { beforeReplace(); File.Replace(temp, output, null); }
                else if (File.Exists(output)) File.Replace(temp, output, null);
                else File.Move(temp, output);
                log("Saved " + (images.Count + retained.Length) + " image(s).");
            }
            finally { TryDelete(temp); }
        }

        public static ZipArchive OpenGallery(string carrier, string password, Action<string> log,
            CancellationToken cancel = default(CancellationToken), Action<int> progress = null)
        {
            using (var fs = File.OpenRead(carrier))
                return OpenGallery(fs, password, log, cancel, progress, out _);
        }

#if WPF_DESKTOP
        public static (long Length, DateTime Modified) Stamp(string path)
        {
            var file = new FileInfo(path);
            return (file.Length, file.LastWriteTimeUtc);
        }

        public static void Edit(string path, (long Length, DateTime Modified) expected, string password,
            IList<string> additions, IReadOnlyDictionary<int, string> removals, Action<string> log, CancellationToken cancel, Action<int> progress)
        {
            void VerifyStamp()
            {
                if (Stamp(path) != expected) throw new IOException("The vault changed on disk. Open it again before editing.");
            }
            using var fs = File.OpenRead(path);
            VerifyStamp();
            using var source = OpenGallery(fs, password, log, cancel, progress, out long prefixLength);
            var rows = source.Entries.Where(e => e.Name.Length != 0).ToArray();
            foreach (var item in removals)
                if (item.Key < 0 || item.Key >= rows.Length || rows[item.Key].FullName != item.Value)
                    throw new IOException("A photo changed. Open the vault again before editing.");
            var retained = rows.Where((_, index) => !removals.ContainsKey(index)).ToArray();
            if (retained.Length + additions.Count < 1) throw new InvalidDataException("Keep at least one image in the vault.");
            WriteVault(path, password, additions, retained, dst =>
            {
                if (prefixLength == 0) return;
                log("Copying source PDF…");
                fs.Position = 0;
                Copy(fs, dst, cancel, Percent(progress, prefixLength), prefixLength);
            }, log, cancel, progress, () => { VerifyStamp(); fs.Dispose(); });
        }
#endif

        static ZipArchive OpenGallery(FileStream fs, string password, Action<string> log, CancellationToken cancel,
            Action<int> progress, out long start)
        {
            cancel.ThrowIfCancellationRequested();
            long size = fs.Length;
            if (size < TrailerLen) throw new InvalidDataException("Not a vault file.");
            {
                fs.Seek(size - TrailerLen, SeekOrigin.Begin);
                var tr = new byte[TrailerLen];
                ReadExactly(fs, tr, 0, tr.Length);

                int off = 0;
                for (int i = 0; i < Magic.Length; i++)
                    if (tr[i] != Magic[i]) throw new InvalidDataException("No vault payload found.");
                off = Magic.Length;
                int version = BitConverter.ToInt32(tr, off);
                if (version != 1 && version != 2) throw new InvalidDataException("Unsupported version.");
                off += 4;
                long length = BitConverter.ToInt64(tr, off); off += 8;
                if (BitConverter.ToInt32(tr, off) != Chunk) throw new InvalidDataException("Invalid vault header.");
                off += 4;
                var salt = new byte[SaltLen]; Array.Copy(tr, off, salt, 0, SaltLen); off += SaltLen;
                var iv = new byte[IvLen]; Array.Copy(tr, off, iv, 0, IvLen); off += IvLen;
                var expected = new byte[MacLen]; Array.Copy(tr, off, expected, 0, MacLen);

                if (length > MaxArchive) throw TooLarge();
                if (length <= 0) throw new InvalidDataException("Corrupt vault.");
                long region = version == 1 ? (length / 16 + 1) * 16 : length;
                start = size - TrailerLen - region;
                if (start < 0 || region % 16 != 0) throw new InvalidDataException("Corrupt vault.");
                if (region > MaxArchive) throw TooLarge();

                log("Deriving key…");
                var keys = Derive(password, salt, version);
                log("Verifying archive…");
                using (var hmac = new HMACSHA256(keys.Skip(32).ToArray()))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    Hash(hmac, fs, region, tr, version == 2 ? off : 0, cancel, progress);
                    if (!FixedTimeEqual(hmac.Hash, expected))
                        throw new InvalidDataException("Wrong password or data was modified.");
                }

                log("Decrypting gallery...");
                cancel.ThrowIfCancellationRequested();
                var bytes = new byte[(int)region];
                var memory = new PlaintextStream(bytes);
                try
                {
                    using (var aes = Aes.Create())
                    {
                        aes.Key = keys.Take(32).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                        fs.Seek(start, SeekOrigin.Begin);
                        using (var transform = aes.CreateDecryptor(aes.Key, iv))
                        {
                            var cipher = new byte[Chunk];
                            var plain = new byte[Chunk];
                            try
                            {
                                long left = region;
                                var decrypting = Percent(progress, region);
                                while (left > 16)
                                {
                                    cancel.ThrowIfCancellationRequested();
                                    int want = (int)Math.Min(cipher.Length, left - 16);
                                    ReadExactly(fs, cipher, 0, want);
                                    int n = transform.TransformBlock(cipher, 0, want, plain, 0);
                                    memory.Write(plain, 0, n);
                                    left -= want;
                                    decrypting(region - left);
                                }
                                var last = new byte[16];
                                ReadExactly(fs, last, 0, 16);
                                var tail = transform.TransformFinalBlock(last, 0, 16);
                                try { memory.Write(tail, 0, tail.Length); }
                                finally { Clear(tail); }
                                decrypting(region);
                            }
                            finally { Clear(plain); }
                        }
                    }

                    memory.SetLength(memory.Position);
                    memory.Position = 0;
                    cancel.ThrowIfCancellationRequested();
                    return new ZipArchive(memory, ZipArchiveMode.Read);
                }
                catch { memory.Dispose(); throw; }
            }
        }

        static InvalidDataException TooLarge() { return new InvalidDataException("Gallery archives are limited to 512 MiB."); }
        sealed class PlaintextStream : MemoryStream
        {
            readonly byte[] backing;
            public PlaintextStream(byte[] bytes) : base(bytes, 0, bytes.Length, true, false) { backing = bytes; }
            protected override void Dispose(bool disposing)
            {
                if (disposing) Clear(backing);
                base.Dispose(disposing);
            }
        }

        static void Clear(byte[] buffer)
        {
#if WPF_DESKTOP
            CryptographicOperations.ZeroMemory(buffer);
#else
            Array.Clear(buffer, 0, buffer.Length);
#endif
        }

        static void Copy(Stream source, Stream target, CancellationToken cancel, Action<long> report, long limit = long.MaxValue)
        {
            var buffer = new byte[128 * 1024];
            try
            {
                long done = 0;
                while (done < limit)
                {
                    cancel.ThrowIfCancellationRequested();
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - done));
                    if (read == 0)
                    {
                        if (limit != long.MaxValue) throw new EndOfStreamException();
                        break;
                    }
                    target.Write(buffer, 0, read);
                    report(done += read);
                }
            }
            finally { Clear(buffer); }
        }

        static Action<long> Percent(Action<int> progress, long total)
        {
            int last = -1;
            return done =>
            {
                int value = total <= 0 ? 100 : (int)Math.Min(100, 100.0 * done / total);
                if (value != last) { last = value; if (progress != null) progress(value); }
            };
        }

        static void Hash(HMACSHA256 hmac, Stream stream, long left, byte[] header, int length,
            CancellationToken cancel, Action<int> progress)
        {
            var buf = new byte[64 * 1024];
            long total = left;
            var report = Percent(progress, total);
            while (left > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buf.Length, left);
                ReadExactly(stream, buf, 0, count);
                hmac.TransformBlock(buf, 0, count, null, 0);
                left -= count;
                report(total - left);
            }
            hmac.TransformFinalBlock(header, 0, length);
        }
        static byte[] RandomBytes(int n) { var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        static void ReadExactly(Stream s, byte[] b, int off, int count)
        { while (count > 0) { int n = s.Read(b, off, count); if (n == 0) throw new EndOfStreamException(); off += n; count -= n; } }
        static bool FixedTimeEqual(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0; }
        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }

    internal static class MiniPdf
    {
        public static byte[] Create()
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            string content = "BT /F1 20 Tf 60 770 Td (CARRIER DOCUMENT) Tj ET\n" +
                             "BT /F1 10 Tf 60 745 Td (ID: " + id + "  Generated: " + DateTime.UtcNow.ToString("u") + ") Tj ET\n";
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                "<< /Length " + Encoding.ASCII.GetByteCount(content) + " >>\nstream\n" + content + "endstream"
            };
            var sb = new StringBuilder("%PDF-1.4\n");
            var offsets = new int[objects.Length + 1];
            for (int i = 0; i < objects.Length; i++)
            {
                offsets[i + 1] = sb.Length;
                sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n').Append("0000000000 65535 f \n");
            for (int i = 1; i <= objects.Length; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }

#if !WPF_DESKTOP
    internal sealed class MainForm : Form
    {
        internal static readonly Color Ink = Color.FromArgb(17, 26, 55), Paper = Color.FromArgb(246, 248, 253),
            Blue = Color.FromArgb(49, 84, 190), Silver = Color.FromArgb(170, 182, 212);
        RadioButton standalone = new RadioButton { Text = "Encrypted gallery only", Checked = true },
                    existing = new RadioButton { Text = "Use existing PDF" },
                    generate = new RadioButton { Text = "Generate a PDF" };
        TextBox pdf = new TextBox { ReadOnly = true }, pass = new TextBox { PasswordChar = '•' },
                confirm = new TextBox { PasswordChar = '•' };
        ListBox images = new ListBox { HorizontalScrollbar = true, IntegralHeight = false };
        Label status = new Label { Text = "Ready." }, rstatus = new Label { Text = "Ready." };
        TextBox carrier = new TextBox { ReadOnly = true },
                rpass = new TextBox { PasswordChar = '•' };
        bool busy;
        const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff";

        public MainForm()
        {
            Text = "PDF Vault";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 535);
            BackColor = Paper; ForeColor = Ink;
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Ink };
            header.Controls.Add(At(new Label { Text = "PDF Vault", ForeColor = Color.White,
                Font = new Font("Segoe UI", 18F, FontStyle.Bold) }, 18, 9, 580, 35));
            header.Controls.Add(At(new Label { Text = "Password-protected image galleries", ForeColor = Silver }, 20, 48, 580));
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(HideTab());
            tabs.TabPages.Add(GalleryTab());
            Controls.Add(tabs);
            Controls.Add(header);
            FormClosing += (s, e) => { if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        }

        static Control At(Control c, int x, int y, int w, int h = 24)
        { c.Left = x; c.Top = y; c.Width = w; c.Height = h; return c; }
        static Label L(string t, int x, int y, int w = 120) { return (Label)At(new Label { Text = t }, x, y + 4, w); }
        static Button B(string t) { return new Button { Text = t, Width = 70, Height = 27 }; }
        static void Primary(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Ink;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 63, 150);
            button.BackColor = Blue;
            button.ForeColor = Color.White;
            button.UseVisualStyleBackColor = false;
        }

        TabPage HideTab()
        {
            var tab = new TabPage("Create") { BackColor = Paper, AutoScroll = true };
            var gs = new GroupBox { Text = "Output format", Bounds = new Rectangle(10, 10, 600, 100) };
            gs.Controls.Add(At(standalone, 15, 18, 165));
            gs.Controls.Add(At(existing, 15, 43, 165));
            gs.Controls.Add(At(generate, 15, 68, 165));
            gs.Controls.Add(At(pdf, 190, 43, 320));
            var bp = B("Browse..."); bp.Left = 520; bp.Top = 42; bp.Click += (s, e) => Pick(pdf, "PDF files|*.pdf");
            pdf.Enabled = bp.Enabled = false;
            existing.CheckedChanged += (s, e) => pdf.Enabled = bp.Enabled = existing.Checked;
            gs.Controls.Add(bp);
            tab.Controls.Add(gs);

            var gi = new GroupBox { Text = "Images", Bounds = new Rectangle(10, 115, 600, 145) };
            images.Bounds = new Rectangle(15, 22, 400, 108); gi.Controls.Add(images);
            var add = B("Add..."); add.Width = 160; add.Left = 425; add.Top = 28;
            add.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Filter = ImageFilter })
                    if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) images.Items.Add(f);
            };
            var clear = B("Clear"); clear.Width = 160; clear.Left = 425; clear.Top = 62;
            clear.Click += (s, e) => images.Items.Clear();
            gi.Controls.Add(add); gi.Controls.Add(clear);
            tab.Controls.Add(gi);

            var gp = new GroupBox { Text = "Passphrase", Bounds = new Rectangle(10, 270, 600, 80) };
            gp.Controls.Add(L("Password", 15, 22, 90)); gp.Controls.Add(At(pass, 110, 20, 210));
            gp.Controls.Add(L("Confirm", 340, 22, 85)); gp.Controls.Add(At(confirm, 430, 20, 150));
            gp.Controls.Add(L("PBKDF2-SHA256 + AES-256-CBC + HMAC-SHA256. No recovery.", 15, 50, 560));
            tab.Controls.Add(gp);

            var hide = new Button { Text = "Create vault…", Width = 150, Height = 34, Left = 10, Top = 370 };
            Primary(hide);
            hide.Click += async (s, e) => await Run(hide, status, async log =>
            {
                if (images.Items.Count == 0) throw new InvalidOperationException("Add at least one image.");
                if (pass.Text.Length == 0) throw new InvalidOperationException("Enter a passphrase.");
                if (pass.Text != confirm.Text) throw new InvalidOperationException("Passwords do not match.");
                if (existing.Checked && !File.Exists(pdf.Text)) throw new InvalidOperationException("Choose a source PDF.");
                var list = images.Items.Cast<string>().ToList();
                string source = existing.Checked ? pdf.Text : null, password = pass.Text, path;
                bool makePdf = generate.Checked;
                using (var dialog = new SaveFileDialog { Filter = standalone.Checked ? "Encrypted gallery|*.pvault" : "PDF files|*.pdf",
                    FileName = standalone.Checked ? "photos.pvault" : "carrier.pdf" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.FileName;
                }
                await Task.Run(() => Vault.Hide(source, makePdf, list, password, path, log));
                pass.Clear(); confirm.Clear();
            });
            tab.Controls.Add(hide);
            tab.Controls.Add(At(status, 175, 379, 440));
            return tab;
        }

        TabPage GalleryTab()
        {
            var tab = new TabPage("Gallery") { BackColor = Paper, AutoScroll = true };
            var gc = new GroupBox { Text = "Encrypted gallery or vault PDF", Bounds = new Rectangle(10, 10, 600, 58) };
            gc.Controls.Add(At(carrier, 15, 22, 430));
            var b1 = B("Browse..."); b1.Width = 130; b1.Left = 455; b1.Top = 21;
            b1.Click += (s, e) => Pick(carrier, "Vault files|*.pvault;*.pdf|All files|*.*");
            gc.Controls.Add(b1); tab.Controls.Add(gc);

            var gp = new GroupBox { Text = "Passphrase", Bounds = new Rectangle(10, 80, 600, 58) };
            gp.Controls.Add(At(rpass, 15, 22, 300));
            gp.Controls.Add(L("Same passphrase used to create the vault", 330, 24, 260));
            tab.Controls.Add(gp);

            var reveal = new Button { Text = "Open gallery", Width = 150, Height = 34, Left = 10, Top = 150 };
            Primary(reveal);
            reveal.Click += async (s, e) => await Run(reveal, rstatus, async log =>
            {
                if (!File.Exists(carrier.Text)) throw new InvalidOperationException("Choose a vault file.");
                if (rpass.Text.Length == 0) throw new InvalidOperationException("Enter the passphrase.");
                string path = carrier.Text, password = rpass.Text;
                using (var archive = await Task.Run(() => Vault.OpenGallery(path, password, log)))
                {
                    rpass.Clear();
                    using (var gallery = new GalleryForm(archive)) gallery.ShowDialog(this);
                }
                rstatus.Text = "Gallery closed.";
            });
            tab.Controls.Add(reveal);
            tab.Controls.Add(At(rstatus, 175, 159, 440));
            return tab;
        }

        static void Pick(TextBox box, string filter)
        { using (var d = new OpenFileDialog { Filter = filter }) if (d.ShowDialog() == DialogResult.OK) box.Text = d.FileName; }

        async Task Run(Button button, Label label, Func<Action<string>, Task> work)
        {
            if (busy) return;
            busy = true;
            button.Enabled = false;
            IProgress<string> progress = new Progress<string>(message => label.Text = message);
            try { await work(progress.Report); }
            catch (Exception ex) { label.Text = "Failed."; MessageBox.Show(this, ex.Message, "PDF Vault"); }
            finally { button.Enabled = true; busy = false; }
        }
    }

    internal sealed class GalleryForm : Form
    {
        readonly ListBox photos = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "Photos" };
        readonly Label caption = new Label { Dock = DockStyle.Bottom, Height = 34, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0), AutoEllipsis = true };
        readonly Panel canvas = new Panel { Dock = DockStyle.Fill, BackColor = MainForm.Ink };
        Bitmap photo;
        bool loading;

        public GalleryForm(ZipArchive archive)
        {
            Text = "PDF Vault — Gallery";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(840, 520);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = MainForm.Paper; ForeColor = MainForm.Ink;
            var split = new SplitContainer { Size = ClientSize, Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Controls.Add(split);
            split.SplitterDistance = 220;
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
            photos.Items.AddRange(entries.Select(e => e.Name).ToArray());
            split.Panel1.Controls.Add(photos);
            split.Panel2.Controls.Add(canvas);
            split.Panel2.Controls.Add(caption);
            canvas.Resize += (s, e) => canvas.Invalidate();
            canvas.Paint += (s, e) =>
            {
                if (photo == null) return;
                float scale = Math.Min(1, Math.Min(canvas.Width / (float)photo.Width, canvas.Height / (float)photo.Height));
                if (scale <= 0) return;
                int w = Math.Max(1, (int)(photo.Width * scale)), h = Math.Max(1, (int)(photo.Height * scale));
                var rect = new Rectangle((canvas.Width - w) / 2, (canvas.Height - h) / 2, w, h);
                if (scale == 1) e.Graphics.DrawImageUnscaled(photo, rect.Location);
                else { e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; e.Graphics.DrawImage(photo, rect); }
            };
            photos.SelectedIndexChanged += async (s, e) =>
            {
                int index = photos.SelectedIndex;
                if (loading || index < 0) return;
                if (photo != null) photo.Dispose();
                photo = null; canvas.Invalidate();
                loading = true; photos.Enabled = false;
                caption.Text = "Loading photo…";
                try
                {
                    photo = await Task.Run(() => Decode(entries[index]));
                    caption.Text = string.Format("{0} / {1}   ·   {2}   ·   {3} × {4}", index + 1, entries.Length, entries[index].Name, photo.Width, photo.Height);
                }
                catch (Exception ex) { caption.Text = "Cannot preview this photo: " + ex.Message; }
                finally { loading = false; photos.Enabled = true; photos.Focus(); canvas.Invalidate(); }
            };
            Shown += (s, e) => { if (entries.Length > 0) photos.SelectedIndex = 0; else caption.Text = "No photos in this vault."; };
            FormClosing += (s, e) => { if (loading && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            FormClosed += (s, e) => { if (photo != null) photo.Dispose(); };
        }

        internal static Bitmap Decode(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0 || entry.Length > 128L * 1024 * 1024) throw new InvalidDataException("Photo exceeds the 128 MiB preview limit or is empty.");
            var bytes = new byte[(int)entry.Length];
            using (var stream = entry.Open())
            {
                int total = 0;
                while (total < bytes.Length)
                {
                    int read = stream.Read(bytes, total, bytes.Length - total);
                    if (read == 0) throw new InvalidDataException("Photo data is incomplete.");
                    total += read;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("Invalid photo length.");
            }
            using (var memory = new MemoryStream(bytes, false))
            using (var image = Image.FromStream(memory, true, true))
            {
                if ((long)image.Width * image.Height > 64000000L) throw new InvalidDataException("Photo exceeds the 64 megapixel preview limit.");
                return new Bitmap(image);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
#endif
}
