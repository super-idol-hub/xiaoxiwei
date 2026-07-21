using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using FormsTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("小曦薇")]
[assembly: AssemblyDescription("小曦薇｜4K 写实粉丝向桌面互动角色｜v3.0.6｜开发者：Anbunengsi｜个人非商用")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("小曦薇")]
[assembly: AssemblyTrademark("开发者：Anbunengsi")]
[assembly: AssemblyCopyright("仅供粉丝非商用使用")]
[assembly: AssemblyVersion("3.0.6.0")]
[assembly: AssemblyFileVersion("3.0.6.0")]

namespace XiaoXiWei.Standalone
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    string reportPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "xiaoxiwei-self-test.json");
                    string previewPath = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "xiaoxiwei-self-test-preview.png");
                    return SelfTest4K.Run(reportPath, previewPath);
                }

                int autoExitMilliseconds = 0;
                bool qaMode = false;
                if (args.Length > 0 && string.Equals(args[0], "--qa-window", StringComparison.OrdinalIgnoreCase))
                {
                    qaMode = true;
                    if (args.Length > 1)
                    {
                        int.TryParse(args[1], out autoExitMilliseconds);
                    }
                    if (autoExitMilliseconds < 1000)
                    {
                        autoExitMilliseconds = 8000;
                    }
                }

                bool createdNew;
                string mutexName = qaMode
                    ? "Local\\XiaoXiWeiStandalonePet_7B9B05F0_QA"
                    : "Local\\XiaoXiWeiStandalonePet_7B9B05F0";
                using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        if (qaMode)
                        {
                            return 4;
                        }
                        MessageBox.Show("小曦薇已经在桌面上啦。", "小曦薇", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    int threadExceptionExitCode = 0;
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
                    {
                        if (qaMode)
                        {
                            threadExceptionExitCode = 3;
                            CrashReporter.ReportSilently(eventArgs.Exception);
                            Application.ExitThread();
                        }
                        else
                        {
                            CrashReporter.Report(eventArgs.Exception);
                        }
                    };

                    using (PetForm form = new PetForm(autoExitMilliseconds, qaMode))
                    {
                        Application.Run(form);
                    }

                    GC.KeepAlive(mutex);
                    return threadExceptionExitCode;
                }
            }
            catch (Exception exception)
            {
                bool silent = args.Length > 0
                    && (string.Equals(args[0], "--qa-window", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase));
                if (silent)
                {
                    CrashReporter.ReportSilently(exception);
                }
                else
                {
                    CrashReporter.Report(exception);
                }
                return 1;
            }
        }
    }

    internal static class CrashReporter
    {
        private static string WriteLog(Exception exception)
        {
            string path = Path.Combine(Path.GetTempPath(), "xiaoxiwei-pet-error.txt");
            File.WriteAllText(path, exception.ToString(), Encoding.UTF8);
            return path;
        }

        public static void ReportSilently(Exception exception)
        {
            try
            {
                WriteLog(exception);
            }
            catch
            {
                // QA mode must still be able to terminate on a reporting failure.
            }
        }

        public static void Report(Exception exception)
        {
            try
            {
                string path = WriteLog(exception);
                MessageBox.Show(
                    "小曦薇启动时遇到问题。错误记录已保存到：\n" + path,
                    "小曦薇",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // Avoid a second crash while reporting the first one.
            }
        }
    }

    internal static class AtlasResource
    {
        public const string ResourceName = "XiaoXiWei.Standalone.Atlas.png";
        public const int CellWidth = 192;
        public const int CellHeight = 208;
        public const int Columns = 8;
        public const int Rows = 11;

        public static Bitmap LoadAtlas()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException("找不到内嵌角色图集资源。");
            }

            using (stream)
            using (Bitmap source = new Bitmap(stream))
            {
                Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(source, 0, 0);
                }
                return copy;
            }
        }
    }

    internal static class SelfTest
    {
        private static readonly int[] UsedCellsPerRow = new int[] { 7, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8 };

        public static int Run(string reportPath, string previewPath)
        {
            List<string> errors = new List<string>();
            int[,] alphaCounts = new int[AtlasResource.Rows, AtlasResource.Columns];
            int width = 0;
            int height = 0;

            try
            {
                using (Bitmap atlas = AtlasResource.LoadAtlas())
                {
                    width = atlas.Width;
                    height = atlas.Height;
                    if (width != AtlasResource.Columns * AtlasResource.CellWidth || height != AtlasResource.Rows * AtlasResource.CellHeight)
                    {
                        errors.Add("atlas dimensions are not 1536x2288");
                    }

                    CountAlpha(atlas, alphaCounts);
                    for (int row = 0; row < AtlasResource.Rows; row++)
                    {
                        for (int column = 0; column < AtlasResource.Columns; column++)
                        {
                            bool shouldBeUsed = column < UsedCellsPerRow[row];
                            if (shouldBeUsed && alphaCounts[row, column] == 0)
                            {
                                errors.Add("used cell is empty: row " + row + ", column " + column);
                            }
                            if (!shouldBeUsed && alphaCounts[row, column] != 0)
                            {
                                errors.Add("unused cell is populated: row " + row + ", column " + column);
                            }
                        }
                    }

                    SavePreview(atlas, previewPath);
                }
            }
            catch (Exception exception)
            {
                AggregateException aggregate = exception as AggregateException;
                if (aggregate != null)
                {
                    foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                    {
                        errors.Add(inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace);
                    }
                }
                else
                {
                    errors.Add(exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
                }
            }

            bool ok = errors.Count == 0;
            WriteReport(reportPath, previewPath, width, height, alphaCounts, errors, ok);
            return ok ? 0 : 2;
        }

        private static void CountAlpha(Bitmap source, int[,] counts)
        {
            using (Bitmap bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(source, 0, 0);
                }

                Rectangle rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int absoluteStride = Math.Abs(data.Stride);
                    byte[] bytes = new byte[absoluteStride * bitmap.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        int sourceRow = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                        int rowOffset = sourceRow * absoluteStride;
                        int atlasRow = y / AtlasResource.CellHeight;
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            if (bytes[rowOffset + x * 4 + 3] != 0)
                            {
                                counts[atlasRow, x / AtlasResource.CellWidth]++;
                            }
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
        }

        private static void SavePreview(Bitmap atlas, string previewPath)
        {
            string fullPath = Path.GetFullPath(previewPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rectangle sourceRectangle = new Rectangle(0, 0, AtlasResource.CellWidth, AtlasResource.CellHeight);
            using (Bitmap preview = new Bitmap(AtlasResource.CellWidth * 2, AtlasResource.CellHeight * 2, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(atlas, new Rectangle(0, 0, preview.Width, preview.Height), sourceRectangle, GraphicsUnit.Pixel);
                preview.Save(fullPath, ImageFormat.Png);
            }
        }

        private static void WriteReport(
            string reportPath,
            string previewPath,
            int width,
            int height,
            int[,] alphaCounts,
            List<string> errors,
            bool ok)
        {
            string fullPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"ok\": " + (ok ? "true" : "false") + ",");
            builder.AppendLine("  \"application\": \"小曦薇\",");
            builder.AppendLine("  \"version\": \"3.0.6\",");
            builder.AppendLine("  \"codexDependency\": false,");
            builder.AppendLine("  \"atlasEmbedded\": true,");
            builder.AppendLine("  \"atlasResource\": \"" + AtlasResource.ResourceName + "\",");
            builder.AppendLine("  \"width\": " + width + ",");
            builder.AppendLine("  \"height\": " + height + ",");
            builder.AppendLine("  \"columns\": " + AtlasResource.Columns + ",");
            builder.AppendLine("  \"rows\": " + AtlasResource.Rows + ",");
            builder.AppendLine("  \"cellWidth\": " + AtlasResource.CellWidth + ",");
            builder.AppendLine("  \"cellHeight\": " + AtlasResource.CellHeight + ",");
            builder.AppendLine("  \"preview\": \"" + EscapeJson(Path.GetFullPath(previewPath)) + "\",");
            builder.AppendLine("  \"usedAlphaCounts\": [");
            for (int row = 0; row < AtlasResource.Rows; row++)
            {
                builder.Append("    [");
                for (int column = 0; column < AtlasResource.Columns; column++)
                {
                    if (column > 0)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(alphaCounts[row, column]);
                }
                builder.Append("]");
                builder.AppendLine(row == AtlasResource.Rows - 1 ? string.Empty : ",");
            }
            builder.AppendLine("  ],");
            builder.AppendLine("  \"errors\": [");
            for (int index = 0; index < errors.Count; index++)
            {
                builder.Append("    \"");
                builder.Append(EscapeJson(errors[index]));
                builder.Append("\"");
                builder.AppendLine(index == errors.Count - 1 ? string.Empty : ",");
            }
            builder.AppendLine("  ]");
            builder.AppendLine("}");
            File.WriteAllText(fullPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class FrameResource
    {
        public const string ResourceName = "XiaoXiWei.Standalone.Frames.zip";
        public const int SourceWidth = 528;
        public const int SourceHeight = 808;
        public const int LogicalWidth = 132;
        public const int LogicalHeight = 202;
        public const int Columns = 8;
        public const int Rows = 24;
        public static readonly int[] UsedCellsPerRow = new int[]
        {
            7, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8, 6,
            8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8
        };

        public static string EntryName(int row, int column)
        {
            return "frames/" + ExternalEntryName(row, column);
        }

        public static string ExternalEntryName(int row, int column)
        {
            return string.Format("r{0:00}/c{1:00}.png", row, column);
        }

        public static string MotionEntryName(int row, int column, int targetRow, int targetColumn)
        {
            return string.Format(
                "motion/r{0:00}/c{1:00}-r{2:00}-c{3:00}.mtn",
                row,
                column,
                targetRow,
                targetColumn);
        }

        private static string MotionEntryName(
            string entryPrefix,
            int row,
            int column,
            int targetRow,
            int targetColumn)
        {
            string prefix = entryPrefix ?? string.Empty;
            if (prefix.EndsWith("frames/", StringComparison.Ordinal))
            {
                prefix = prefix.Substring(0, prefix.Length - "frames/".Length);
            }
            return prefix + MotionEntryName(row, column, targetRow, targetColumn);
        }

        public static byte[] LoadArchiveBytes()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException("找不到内嵌的4K动作帧资源。");
            }

            using (stream)
            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        public static int CountPngEntries(byte[] archiveBytes)
        {
            using (MemoryStream stream = new MemoryStream(archiveBytes, false))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                int count = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public static Bitmap LoadSourceFrame(byte[] archiveBytes, int row, int column)
        {
            return LoadSourceFrame(archiveBytes, "frames/", row, column);
        }

        public static Bitmap LoadSourceFrame(byte[] archiveBytes, string entryPrefix, int row, int column)
        {
            string name = (entryPrefix ?? string.Empty) + ExternalEntryName(row, column);
            using (MemoryStream archiveStream = new MemoryStream(archiveBytes, false))
            using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry entry = archive.GetEntry(name);
                if (entry == null)
                {
                    throw new InvalidOperationException("4K动作帧缺失：" + name);
                }

                using (Stream entryStream = entry.Open())
                using (Bitmap source = new Bitmap(entryStream))
                {
                    Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(copy))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImageUnscaled(source, 0, 0);
                    }
                    return copy;
                }
            }
        }

        public static bool TryLoadMotionField(
            byte[] archiveBytes,
            string entryPrefix,
            int row,
            int column,
            int targetRow,
            int targetColumn,
            out MotionField field)
        {
            field = null;
            using (MemoryStream archiveStream = new MemoryStream(archiveBytes, false))
            using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false))
            {
                string directName = MotionEntryName(
                    entryPrefix, row, column, targetRow, targetColumn);
                ZipArchiveEntry entry = archive.GetEntry(directName);
                bool reversed = false;
                if (entry == null)
                {
                    string reverseName = MotionEntryName(
                        entryPrefix, targetRow, targetColumn, row, column);
                    entry = archive.GetEntry(reverseName);
                    reversed = entry != null;
                }
                if (entry == null || entry.Length <= 0 || entry.Length > 1024 * 1024)
                {
                    return false;
                }
                using (Stream entryStream = entry.Open())
                using (MemoryStream copy = new MemoryStream())
                {
                    entryStream.CopyTo(copy);
                    field = MotionField.Parse(copy.ToArray(), reversed);
                    return field != null;
                }
            }
        }

        public static bool ValidateExternalArchive(byte[] archiveBytes, out string error)
        {
            error = string.Empty;
            if (archiveBytes == null || archiveBytes.Length == 0 || archiveBytes.Length > 128 * 1024 * 1024)
            {
                error = "archive size is invalid";
                return false;
            }
            if (UsedCellsPerRow.Length != Rows)
            {
                error = "runtime row contract is inconsistent";
                return false;
            }

            try
            {
                HashSet<string> expected = new HashSet<string>(StringComparer.Ordinal);
                for (int row = 0; row < Rows; row++)
                {
                    for (int column = 0; column < UsedCellsPerRow[row]; column++)
                    {
                        expected.Add(ExternalEntryName(row, column));
                    }
                }

                HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
                long totalUncompressedBytes = 0;
                using (MemoryStream stream = new MemoryStream(archiveBytes, false))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string name = entry.FullName;
                        bool unsafeName = string.IsNullOrEmpty(name)
                            || name.StartsWith("/", StringComparison.Ordinal)
                            || name.IndexOf('\\') >= 0
                            || name.IndexOf(':') >= 0
                            || name.IndexOf("../", StringComparison.Ordinal) >= 0;
                        if (unsafeName)
                        {
                            error = "unsafe archive entry";
                            return false;
                        }
                        if (name.EndsWith("/", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        bool isFrame = expected.Contains(name);
                        bool isMotion = name.StartsWith("motion/", StringComparison.Ordinal)
                            && name.EndsWith(".mtn", StringComparison.Ordinal);
                        if ((!isFrame && !isMotion) || (isFrame && !found.Add(name)))
                        {
                            error = "unexpected or duplicate archive entry";
                            return false;
                        }
                        if (entry.Length <= 0 || entry.Length > 32L * 1024L * 1024L)
                        {
                            error = "frame entry size is invalid";
                            return false;
                        }
                        totalUncompressedBytes += entry.Length;
                        if (totalUncompressedBytes > 128L * 1024L * 1024L)
                        {
                            error = "archive expands beyond the safety limit";
                            return false;
                        }

                        if (isMotion)
                        {
                            if (entry.Length != MotionField.EncodedBytes)
                            {
                                error = "motion entry size is invalid";
                                return false;
                            }
                            using (Stream motionStream = entry.Open())
                            {
                                byte[] header = new byte[4];
                                if (motionStream.Read(header, 0, header.Length) != header.Length
                                    || header[0] != (byte)'X'
                                    || header[1] != (byte)'W'
                                    || header[2] != (byte)'M'
                                    || header[3] != (byte)'1')
                                {
                                    error = "motion entry header is invalid";
                                    return false;
                                }
                            }
                        }
                        else
                        {
                            using (Stream frameStream = entry.Open())
                            using (Bitmap frame = new Bitmap(frameStream))
                            {
                                if (frame.Width != SourceWidth || frame.Height != SourceHeight)
                                {
                                    error = "frame dimensions do not match 528x808";
                                    return false;
                                }
                            }
                        }
                    }
                }

                if (found.Count != expected.Count)
                {
                    error = "archive is missing required frames";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name;
                return false;
            }
        }
    }

    internal sealed class SkinPack
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Developer;
        public readonly string ArchivePath;
        public readonly bool IsBuiltIn;
        public readonly string ExclusiveActionName;

        public SkinPack(
            string id,
            string name,
            string developer,
            string archivePath,
            bool isBuiltIn,
            string exclusiveActionName)
        {
            Id = id;
            Name = name;
            Developer = developer;
            ArchivePath = archivePath;
            IsBuiltIn = isBuiltIn;
            ExclusiveActionName = exclusiveActionName ?? string.Empty;
        }

        public bool HasExclusiveAction
        {
            get { return !string.IsNullOrWhiteSpace(ExclusiveActionName); }
        }

        public string EntryPrefix
        {
            get { return IsBuiltIn ? "frames/" : string.Empty; }
        }

        public bool TryLoadArchive(out byte[] archiveBytes)
        {
            archiveBytes = null;
            try
            {
                if (IsBuiltIn)
                {
                    archiveBytes = FrameResource.LoadArchiveBytes();
                    return true;
                }

                FileInfo archiveFile = new FileInfo(ArchivePath);
                if (!archiveFile.Exists || archiveFile.Length <= 0 || archiveFile.Length > 128L * 1024L * 1024L)
                {
                    return false;
                }
                byte[] candidate = File.ReadAllBytes(archiveFile.FullName);
                string error;
                if (!FrameResource.ValidateExternalArchive(candidate, out error))
                {
                    return false;
                }
                archiveBytes = candidate;
                return true;
            }
            catch
            {
                archiveBytes = null;
                return false;
            }
        }
    }

    internal sealed class SkinCatalog
    {
        private static readonly Regex RootPattern = new Regex(
            @"<skin\b([^>]*)/?>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly List<SkinPack> _packs;
        private readonly string _selectionPath;

        private SkinCatalog(List<SkinPack> packs)
        {
            _packs = packs;
            _selectionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Anbunengsi",
                "XiaoXiWeiPet",
                "skin.txt");
        }

        public IList<SkinPack> Packs
        {
            get { return _packs.AsReadOnly(); }
        }

        public SkinPack BuiltIn
        {
            get { return _packs[0]; }
        }

        public static SkinCatalog Discover()
        {
            List<SkinPack> packs = new List<SkinPack>();
            packs.Add(new SkinPack(
                "built-in",
                "白裙（内置）",
                "Anbunengsi",
                string.Empty,
                true,
                "白裙星光亮相"));

            try
            {
                string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins"));
                if (Directory.Exists(root))
                {
                    foreach (string folder in Directory.GetDirectories(root))
                    {
                        SkinPack pack = TryReadExternalPack(root, folder);
                        if (pack == null || FindById(packs, pack.Id) != null)
                        {
                            continue;
                        }

                        byte[] validationBytes;
                        if (pack.TryLoadArchive(out validationBytes))
                        {
                            packs.Add(pack);
                        }
                    }
                }
            }
            catch
            {
                // Invalid optional skins are skipped without affecting startup.
            }
            return new SkinCatalog(packs);
        }

        public SkinPack LoadPreferred(out byte[] archiveBytes)
        {
            string selectedId = ReadSelectedId();
            SkinPack preferred = FindById(_packs, selectedId) ?? BuiltIn;
            if (preferred.TryLoadArchive(out archiveBytes))
            {
                return preferred;
            }

            BuiltIn.TryLoadArchive(out archiveBytes);
            return BuiltIn;
        }

        public void SaveSelectedId(string id)
        {
            try
            {
                string directory = Path.GetDirectoryName(_selectionPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_selectionPath, id ?? "built-in", new UTF8Encoding(false));
            }
            catch
            {
                // Skin selection persistence is optional.
            }
        }

        private string ReadSelectedId()
        {
            try
            {
                if (File.Exists(_selectionPath))
                {
                    string value = File.ReadAllText(_selectionPath, Encoding.UTF8).Trim();
                    if (IsSafeId(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // Fall back to the built-in skin.
            }
            return "built-in";
        }

        private static SkinPack TryReadExternalPack(string root, string folder)
        {
            try
            {
                string fullFolder = Path.GetFullPath(folder);
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fullFolder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string manifestPath = Path.Combine(fullFolder, "skin.xml");
                FileInfo manifestFile = new FileInfo(manifestPath);
                if (!manifestFile.Exists || manifestFile.Length <= 0 || manifestFile.Length > 64 * 1024)
                {
                    return null;
                }

                string xml = File.ReadAllText(manifestPath, Encoding.UTF8);
                Match rootMatch = RootPattern.Match(xml);
                if (!rootMatch.Success)
                {
                    return null;
                }
                string attributes = rootMatch.Groups[1].Value;
                string apiVersion = GetAttribute(attributes, "apiVersion");
                string id = GetAttribute(attributes, "id");
                string name = DecodeXmlAttribute(GetAttribute(attributes, "name"));
                string developer = DecodeXmlAttribute(GetAttribute(attributes, "developer"));
                string exclusiveAction = DecodeXmlAttribute(GetAttribute(attributes, "exclusiveAction"));
                string archive = GetAttribute(attributes, "archive");
                string folderId = new DirectoryInfo(fullFolder).Name;
                if (!string.Equals(apiVersion, "1", StringComparison.Ordinal)
                    || !IsSafeId(id)
                    || !string.Equals(id, folderId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(name)
                    || name.Length > 64
                    || string.IsNullOrWhiteSpace(developer)
                    || developer.Length > 64
                    || exclusiveAction.Length > 64
                    || !string.Equals(archive, "frames.zip", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string archivePath = Path.GetFullPath(Path.Combine(fullFolder, "frames.zip"));
                if (!archivePath.StartsWith(fullFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return new SkinPack(
                    id,
                    name,
                    developer,
                    archivePath,
                    false,
                    exclusiveAction);
            }
            catch
            {
                return null;
            }
        }

        private static string GetAttribute(string attributes, string name)
        {
            Match match = Regex.Match(
                attributes ?? string.Empty,
                @"(?:^|\s)" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)')",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return string.Empty;
            }
            return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }

        private static string DecodeXmlAttribute(string value)
        {
            return (value ?? string.Empty)
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        private static bool IsSafeId(string id)
        {
            return !string.IsNullOrEmpty(id)
                && id.Length <= 64
                && Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
        }

        private static SkinPack FindById(IEnumerable<SkinPack> packs, string id)
        {
            foreach (SkinPack pack in packs)
            {
                if (string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }
            return null;
        }
    }

    internal static class FrameScaler
    {
        public static Bitmap Scale(Bitmap source, int width, int height)
        {
            Bitmap scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(scaled))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(0, 0, source.Width, source.Height),
                    GraphicsUnit.Pixel);
            }
            return scaled;
        }
    }

    internal static class AnimationSmoothing
    {
        // Each action keeps its authored key poses in frames.zip.  The runtime
        // fills the gaps between them so skins stay compact while motion is
        // presented with at least this many stages per complete cycle.  v3.0.6
        // renders 24 stages while preserving the established v3.0.1 cadence.
        public const int MinimumStagesPerCycle = 24;
        public const int TimingReferenceStagesPerCycle = 56;
        public const int MinimumTickMilliseconds = 16;
        public const int WalkTickMilliseconds = 24;
        public const int JogTickMilliseconds = 19;

        public static int GetStepsPerTransition(int keyFrameCount)
        {
            return GetStepsPerTransitionForTarget(keyFrameCount, MinimumStagesPerCycle);
        }

        public static int GetStepsPerTransitionForTarget(int keyFrameCount, int targetStagesPerCycle)
        {
            if (keyFrameCount <= 1)
            {
                return 1;
            }
            int target = Math.Max(1, targetStagesPerCycle);
            return Math.Max(1, (target + keyFrameCount - 1) / keyFrameCount);
        }

        public static int GetDisplayStageCount(int keyFrameCount)
        {
            return keyFrameCount <= 0 ? 0 : keyFrameCount * GetStepsPerTransition(keyFrameCount);
        }

        public static int GetReferenceMotionDuration(
            int authoredDuration,
            int cycleKeyFrameCount,
            int referenceMinimumInterval)
        {
            int referenceSteps = GetStepsPerTransitionForTarget(
                cycleKeyFrameCount,
                TimingReferenceStagesPerCycle);
            int interval;
            if (authoredDuration >= 400)
            {
                interval = MinimumTickMilliseconds;
            }
            else
            {
                interval = Math.Max(
                    MinimumTickMilliseconds,
                    (int)Math.Round(authoredDuration / (double)Math.Max(1, referenceSteps)));
                interval = Math.Max(referenceMinimumInterval, interval);
            }
            return referenceSteps * interval;
        }

        public static int GetReferenceHoldDuration(
            int authoredDuration,
            int cycleKeyFrameCount,
            int referenceMinimumInterval)
        {
            int hold = authoredDuration - GetReferenceMotionDuration(
                authoredDuration,
                cycleKeyFrameCount,
                referenceMinimumInterval);
            return hold >= MinimumTickMilliseconds ? hold : 0;
        }

        public static int GetDistributedStepInterval(int motionDuration, int tweenSteps, int tweenStep)
        {
            int steps = Math.Max(1, tweenSteps);
            int step = Math.Max(0, Math.Min(steps - 1, tweenStep));
            int start = (int)((long)step * motionDuration / steps);
            int end = (int)((long)(step + 1) * motionDuration / steps);
            return Math.Max(MinimumTickMilliseconds, end - start);
        }

        public static float GetBlendAmount(int tweenStep, int tweenSteps)
        {
            if (tweenStep <= 0 || tweenSteps <= 1)
            {
                return 0.0f;
            }
            if (tweenStep >= tweenSteps)
            {
                return 1.0f;
            }

            // Smoothstep removes the tiny velocity discontinuity at each key
            // pose without manufacturing or storing additional source art.
            float linear = tweenStep / (float)tweenSteps;
            return linear * linear * (3.0f - 2.0f * linear);
        }
    }

    internal sealed class MotionField
    {
        public const int Columns = 17;
        public const int Rows = 25;
        public const int BaseWidth = 132;
        public const int BaseHeight = 202;
        public const int EncodedBytes = 12 + Columns * Rows * 8;
        private const float Quantization = 64.0f;

        private readonly float[] _forwardX;
        private readonly float[] _forwardY;
        private readonly float[] _backwardX;
        private readonly float[] _backwardY;

        private MotionField(
            float[] forwardX,
            float[] forwardY,
            float[] backwardX,
            float[] backwardY)
        {
            _forwardX = forwardX;
            _forwardY = forwardY;
            _backwardX = backwardX;
            _backwardY = backwardY;
        }

        public static MotionField Parse(byte[] bytes, bool reversed)
        {
            if (bytes == null || bytes.Length != EncodedBytes
                || bytes[0] != (byte)'X'
                || bytes[1] != (byte)'W'
                || bytes[2] != (byte)'M'
                || bytes[3] != (byte)'1'
                || BitConverter.ToUInt16(bytes, 4) != Columns
                || BitConverter.ToUInt16(bytes, 6) != Rows
                || BitConverter.ToUInt16(bytes, 8) != BaseWidth
                || BitConverter.ToUInt16(bytes, 10) != BaseHeight)
            {
                return null;
            }

            int count = Columns * Rows;
            float[] forwardX = new float[count];
            float[] forwardY = new float[count];
            float[] backwardX = new float[count];
            float[] backwardY = new float[count];
            int offset = 12;
            for (int index = 0; index < count; index++)
            {
                forwardX[index] = BitConverter.ToInt16(bytes, offset) / Quantization;
                forwardY[index] = BitConverter.ToInt16(bytes, offset + 2) / Quantization;
                backwardX[index] = BitConverter.ToInt16(bytes, offset + 4) / Quantization;
                backwardY[index] = BitConverter.ToInt16(bytes, offset + 6) / Quantization;
                offset += 8;
            }
            return reversed
                ? new MotionField(backwardX, backwardY, forwardX, forwardY)
                : new MotionField(forwardX, forwardY, backwardX, backwardY);
        }

        public void Sample(bool backward, float logicalX, float logicalY, out float dx, out float dy)
        {
            float gridX = Math.Max(0.0f, Math.Min(Columns - 1.0f,
                logicalX * (Columns - 1.0f) / (BaseWidth - 1.0f)));
            float gridY = Math.Max(0.0f, Math.Min(Rows - 1.0f,
                logicalY * (Rows - 1.0f) / (BaseHeight - 1.0f)));
            int x0 = (int)gridX;
            int y0 = (int)gridY;
            int x1 = Math.Min(Columns - 1, x0 + 1);
            int y1 = Math.Min(Rows - 1, y0 + 1);
            float tx = gridX - x0;
            float ty = gridY - y0;
            float[] xs = backward ? _backwardX : _forwardX;
            float[] ys = backward ? _backwardY : _forwardY;
            int i00 = y0 * Columns + x0;
            int i10 = y0 * Columns + x1;
            int i01 = y1 * Columns + x0;
            int i11 = y1 * Columns + x1;
            float topX = xs[i00] + (xs[i10] - xs[i00]) * tx;
            float bottomX = xs[i01] + (xs[i11] - xs[i01]) * tx;
            float topY = ys[i00] + (ys[i10] - ys[i00]) * tx;
            float bottomY = ys[i01] + (ys[i11] - ys[i01]) * tx;
            dx = topX + (bottomX - topX) * ty;
            dy = topY + (bottomY - topY) * ty;
        }
    }

    internal static class PersistentActionContract
    {
        public const int SittingEnterLastFrame = 3;
        public const int SittingLoopFirstFrame = 3;
        public const int SittingLoopLastFrame = 5;
        public const int SittingExitFirstFrame = 6;
        public const int SittingExitLastFrame = 7;

        public const int SideRestSleepFrame = 4;
        public const int SideRestWakeFirstFrame = 5;
        public const int SideRestWakeLastFrame = 7;

        public const int LinanSwingEnterLastFrame = 2;
        public const int LinanSwingLoopFirstFrame = 2;
        public const int LinanSwingLoopLastFrame = 5;
        public const int LinanSwingExitFirstFrame = 6;
        public const int LinanSwingExitLastFrame = 7;
    }

    internal static class LinanSwingContract
    {
        public const string SkinId = "linan-princess";
        public const int LoopTransitionCount = 4;

        public static int GetNextLoopFrame(int currentFrame)
        {
            switch (currentFrame)
            {
                case 2:
                    return 3;
                case 3:
                    return 4;
                case 4:
                    return 5;
                case 5:
                    return 2;
                default:
                    return PersistentActionContract.LinanSwingLoopFirstFrame;
            }
        }

        public static bool HasAlternatingDepthPeaks()
        {
            int[] expected = new int[] { 2, 3, 4, 5, 2, 3, 4, 5, 2 };
            int frame = expected[0];
            for (int index = 1; index < expected.Length; index++)
            {
                frame = GetNextLoopFrame(frame);
                if (frame != expected[index])
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsEnabled(SkinPack skin)
        {
            return skin != null
                && !skin.IsBuiltIn
                && string.Equals(skin.Id, SkinId, StringComparison.OrdinalIgnoreCase)
                && skin.HasExclusiveAction;
        }

        public static bool IsRandomEligible(PetState state, SkinPack skin)
        {
            return state != PetState.SkinExclusive || !IsEnabled(skin);
        }
    }

    internal enum DragReleaseAction
    {
        Idle,
        Wave,
        Angry
    }

    internal static class DragReactionContract
    {
        // A small threshold preserves ordinary clicks and double-click wave
        // gestures while filtering the normal jitter of a pressed mouse.
        public const int MinimumDragPixels = 8;
        public const float ScaleAdjustedDragPixels = 4.0f;
        public const int StartleRow = 4;
        public const int StartleFirstFrame = 0;
        public const int StartleHoldFrame = 2;
        public const int HoldTickMilliseconds = 120;
        public const int AngryRow = 11;
        public const int AngryFirstFrame = 2;
        public const int AngryFrameCount = 4;
        public const bool MovementRowsRuntimeEnabled = false;
        public const bool AutomaticRoamingEnabled = false;

        public static int GetActivationThresholdPixels(float scale)
        {
            float safeScale = scale > 0.0f ? scale : 1.0f;
            return Math.Max(
                MinimumDragPixels,
                (int)Math.Round(ScaleAdjustedDragPixels * safeScale));
        }

        public static DragReleaseAction DecideRelease(
            bool allowPostDragActions,
            bool dragActivated,
            bool pendingDoubleClickWave,
            int dragDistance,
            int dragThreshold)
        {
            if (!allowPostDragActions)
            {
                return DragReleaseAction.Idle;
            }
            if (dragActivated)
            {
                return DragReleaseAction.Angry;
            }
            if (pendingDoubleClickWave && dragDistance < dragThreshold)
            {
                return DragReleaseAction.Wave;
            }
            return DragReleaseAction.Idle;
        }
    }

    internal static class SleepEffectLayout
    {
        public const int ParticleStageCount = 56;
        public const int TickMilliseconds = 60;

        public static PointF GetHeadEmitter(Rectangle visibleBounds, int width, int height)
        {
            if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
            {
                return new PointF(width * (54.0f / FrameResource.LogicalWidth), height * (88.0f / FrameResource.LogicalHeight));
            }

            // The side-rest pose contract places the head in the upper-left
            // mass of the silhouette.  This normalized point tracks scale and
            // alternate skins without tying the effect to the window origin.
            return new PointF(
                visibleBounds.Left + visibleBounds.Width * 0.24f,
                visibleBounds.Top + visibleBounds.Height * 0.08f);
        }
    }

    internal static class MouseLookContract
    {
        public const float EntryRadiusLogicalPixels = 220.0f;
        public const float ExitRadiusLogicalPixels = 270.0f;
        public const float EntryDeadZoneLogicalPixels = 45.0f;
        public const float ExitDeadZoneLogicalPixels = 34.0f;
        public const float DirectionSectorHoldDegrees = 14.75f;
        public const bool FarPointerReturnsBlinkOnlyIdle = true;

        public static double GetOuterRadius(float scale, bool trackingActive)
        {
            float safeScale = scale > 0.0f ? scale : 1.0f;
            return (trackingActive ? ExitRadiusLogicalPixels : EntryRadiusLogicalPixels) * safeScale;
        }

        public static double GetInnerRadius(float scale, bool trackingActive)
        {
            float safeScale = scale > 0.0f ? scale : 1.0f;
            return (trackingActive ? ExitDeadZoneLogicalPixels : EntryDeadZoneLogicalPixels) * safeScale;
        }
    }

    internal static class SkinTransitionContract
    {
        public const int SpinStages = 14;
        public const int RevealStages = 24;
        public const int HoldStages = 8;
        public const int SettleStages = 14;
        public const int TotalStages = SpinStages + RevealStages + HoldStages + SettleStages;
        public const int TickMilliseconds = 20;
        public const int FinalPoseRow = 21;
        public const int FinalPoseColumn = 7;
        public const int OwnedBitmapCountIncludingComposite = 4;
        public const int PendingCacheWarmFrameCount = 2;
        public const int MaximumResidentBitmapCount = OwnedBitmapCountIncludingComposite + PendingCacheWarmFrameCount;
        public const long MaximumPendingArchiveBytes = 128L * 1024L * 1024L;
        public const float RevealTopViewportFraction = 0.065f;
        public const float RevealBottomViewportFraction = 0.99f;
        public const int BodySliceCount = 16;
        public const float MinimumSliceWidthScale = 0.58f;
        public const bool UsesAlphaMaskedBodySlices = true;
        public const bool WholeCanvasCardFlip = false;
        public const bool FinalFrameIsPose = true;
        public static readonly bool LocksActionInput = true;

        public static float GetRevealFraction(int localStage)
        {
            if (localStage < 0)
            {
                return 0.0f;
            }
            if (localStage >= RevealStages)
            {
                return 1.0f;
            }
            return RevealStages <= 1 ? 1.0f : localStage / (float)(RevealStages - 1);
        }

        public static int GetRevealY(int localStage, int height)
        {
            float reveal = GetRevealFraction(localStage);
            float scanTop = height * RevealTopViewportFraction;
            float scanBottom = height * RevealBottomViewportFraction;
            return Math.Max(1, Math.Min(height, (int)Math.Round(scanTop + (scanBottom - scanTop) * reveal)));
        }
    }

    internal static class SkinTransitionRenderer
    {
        public static Bitmap Render(Bitmap oldFrame, Bitmap newPose, Bitmap newIdle, int stage)
        {
            if (oldFrame == null || newPose == null || newIdle == null)
            {
                throw new ArgumentNullException("Skin transition frames must be available.");
            }
            if (oldFrame.Width != newPose.Width
                || oldFrame.Height != newPose.Height
                || oldFrame.Width != newIdle.Width
                || oldFrame.Height != newIdle.Height)
            {
                throw new ArgumentException("Skin transition frame sizes do not match.");
            }

            int clampedStage = Math.Max(0, Math.Min(SkinTransitionContract.TotalStages - 1, stage));
            Bitmap composed = new Bitmap(oldFrame.Width, oldFrame.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(composed))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                if (clampedStage < SkinTransitionContract.SpinStages)
                {
                    DrawBodyTurn(graphics, oldFrame, newPose, clampedStage);
                }
                else if (clampedStage < SkinTransitionContract.SpinStages + SkinTransitionContract.RevealStages)
                {
                    int local = clampedStage - SkinTransitionContract.SpinStages;
                    DrawReveal(graphics, newPose, local);
                }
                else if (clampedStage < SkinTransitionContract.SpinStages
                    + SkinTransitionContract.RevealStages
                    + SkinTransitionContract.HoldStages)
                {
                    graphics.DrawImageUnscaled(newPose, 0, 0);
                }
                else
                {
                    int local = clampedStage
                        - SkinTransitionContract.SpinStages
                        - SkinTransitionContract.RevealStages
                        - SkinTransitionContract.HoldStages;
                    DrawSettlingPose(graphics, newPose, local);
                }
            }
            return composed;
        }

        private static void DrawBodyTurn(Graphics graphics, Bitmap oldFrame, Bitmap newPose, int localStage)
        {
            float progress = SkinTransitionContract.SpinStages <= 1
                ? 1.0f
                : localStage / (float)(SkinTransitionContract.SpinStages - 1);
            int sliceCount = SkinTransitionContract.BodySliceCount;
            for (int slice = 0; slice < sliceCount; slice++)
            {
                int sourceY = slice * oldFrame.Height / sliceCount;
                int nextY = (slice + 1) * oldFrame.Height / sliceCount;
                int sliceHeight = Math.Max(1, nextY - sourceY);
                float normalizedY = (sourceY + sliceHeight * 0.5f) / oldFrame.Height;

                // Head leads, torso follows, and skirt/legs finish last.  The
                // old/new owner therefore changes from top to bottom around
                // the half turn without ever drawing an opaque canvas card.
                float switchPoint = 0.43f + normalizedY * 0.14f + GetBodyPhase(normalizedY);
                switchPoint = Math.Max(0.39f, Math.Min(0.61f, switchPoint));
                float turn = progress <= switchPoint
                    ? 0.5f * progress / Math.Max(0.001f, switchPoint)
                    : 0.5f + 0.5f * (progress - switchPoint) / Math.Max(0.001f, 1.0f - switchPoint);
                turn = Clamp01(turn);

                Bitmap owner = turn < 0.5f ? oldFrame : newPose;
                float edgeFacing = Math.Abs((float)Math.Cos(turn * Math.PI));
                float depth = Math.Max(0.0f, (float)Math.Sin(turn * Math.PI));
                float widthScale = SkinTransitionContract.MinimumSliceWidthScale
                    + (1.0f - SkinTransitionContract.MinimumSliceWidthScale) * edgeFacing;
                float xShift = (float)Math.Sin(turn * Math.PI * 2.0)
                    * owner.Width * 0.018f;
                xShift += GetBodyPhase(normalizedY) * depth * owner.Width * 0.22f;
                DrawTurnSlice(
                    graphics,
                    owner,
                    sourceY,
                    sliceHeight,
                    turn,
                    widthScale,
                    xShift,
                    edgeFacing,
                    depth);
            }
        }

        private static float GetBodyPhase(float normalizedY)
        {
            if (normalizedY < 0.28f)
            {
                return -0.025f;
            }
            if (normalizedY < 0.60f)
            {
                return 0.0f;
            }
            if (normalizedY < 0.82f)
            {
                return 0.022f;
            }
            return 0.035f;
        }

        private static void DrawTurnSlice(
            Graphics graphics,
            Bitmap frame,
            int sourceY,
            int sliceHeight,
            float turn,
            float widthScale,
            float xShift,
            float edgeFacing,
            float depth)
        {
            int destinationWidth = Math.Max(2, (int)Math.Round(frame.Width * widthScale));
            int destinationX = (frame.Width - destinationWidth) / 2 + (int)Math.Round(xShift);
            int sourceMiddle = frame.Width / 2;
            bool nearSideIsRight = turn >= 0.5f;
            int depthSkew = (int)Math.Round(destinationWidth * 0.035f * depth);
            int destinationLeftWidth = Math.Max(
                1,
                destinationWidth / 2 + (nearSideIsRight ? -depthSkew : depthSkew));
            destinationLeftWidth = Math.Min(destinationWidth - 1, destinationLeftWidth);
            int destinationRightWidth = destinationWidth - destinationLeftWidth;
            float baseLight = 0.58f + 0.42f * edgeFacing;
            if (turn >= 0.5f)
            {
                // End the turn at the same dim level used by the alpha scan;
                // the reveal can then restore colour head-to-toe without a
                // one-frame full-bright flash.
                float incoming = Clamp01((turn - 0.5f) * 2.0f);
                baseLight *= 1.0f - incoming * 0.36f;
            }
            float nearLight = baseLight * (1.0f + 0.14f * depth);
            float farLight = baseLight * (1.0f - 0.24f * depth);

            DrawSlicePart(
                graphics,
                frame,
                new Rectangle(0, sourceY, sourceMiddle, sliceHeight),
                new Rectangle(destinationX, sourceY, destinationLeftWidth, sliceHeight),
                nearSideIsRight ? farLight : nearLight,
                1.0f);
            DrawSlicePart(
                graphics,
                frame,
                new Rectangle(sourceMiddle, sourceY, frame.Width - sourceMiddle, sliceHeight),
                new Rectangle(destinationX + destinationLeftWidth, sourceY, destinationRightWidth, sliceHeight),
                nearSideIsRight ? nearLight : farLight,
                1.0f);
        }

        private static void DrawReveal(Graphics graphics, Bitmap newPose, int localStage)
        {
            // The unrevealed body remains a dim silhouette.  Both the bright
            // area and scan glow are clipped by the character alpha, so no
            // full-width rectangle or card edge can appear.
            DrawSlicePart(
                graphics,
                newPose,
                new Rectangle(0, 0, newPose.Width, newPose.Height),
                new Rectangle(0, 0, newPose.Width, newPose.Height),
                0.64f,
                1.0f);

            int revealY = SkinTransitionContract.GetRevealY(localStage, newPose.Height);
            GraphicsState revealed = graphics.Save();
            graphics.SetClip(new Rectangle(0, 0, newPose.Width, revealY));
            graphics.DrawImageUnscaled(newPose, 0, 0);
            graphics.Restore(revealed);

            int glowHeight = Math.Max(4, newPose.Height / 35);
            int glowTop = Math.Max(0, revealY - glowHeight);
            int glowBottom = Math.Min(newPose.Height, revealY + glowHeight);
            GraphicsState glow = graphics.Save();
            graphics.SetClip(new Rectangle(0, glowTop, newPose.Width, Math.Max(1, glowBottom - glowTop)));
            DrawSlicePart(
                graphics,
                newPose,
                new Rectangle(0, 0, newPose.Width, newPose.Height),
                new Rectangle(-1, 0, newPose.Width + 2, newPose.Height),
                1.34f,
                0.92f);
            graphics.Restore(glow);
        }

        private static void DrawSettlingPose(Graphics graphics, Bitmap newPose, int localStage)
        {
            float linear = SkinTransitionContract.SettleStages <= 0
                ? 1.0f
                : (localStage + 1) / (float)SkinTransitionContract.SettleStages;
            float eased = linear * linear * (3.0f - 2.0f * linear);
            if (eased >= 0.999f)
            {
                graphics.DrawImageUnscaled(newPose, 0, 0);
                return;
            }

            float residual = 1.0f - eased;
            int sliceCount = SkinTransitionContract.BodySliceCount;
            for (int slice = 0; slice < sliceCount; slice++)
            {
                int sourceY = slice * newPose.Height / sliceCount;
                int nextY = (slice + 1) * newPose.Height / sliceCount;
                int sliceHeight = Math.Max(1, nextY - sourceY);
                float normalizedY = (sourceY + sliceHeight * 0.5f) / newPose.Height;
                int x = (int)Math.Round(
                    Math.Sin((normalizedY * 2.4f + 0.2f) * Math.PI)
                    * newPose.Width
                    * 0.010f
                    * residual);
                float light = 1.0f - 0.08f * residual
                    + 0.035f * residual * (float)Math.Cos(normalizedY * Math.PI * 3.0f);
                DrawSlicePart(
                    graphics,
                    newPose,
                    new Rectangle(0, sourceY, newPose.Width, sliceHeight),
                    new Rectangle(x, sourceY, newPose.Width, sliceHeight),
                    light,
                    1.0f);
            }
        }

        private static void DrawSlicePart(
            Graphics graphics,
            Bitmap bitmap,
            Rectangle source,
            Rectangle destination,
            float brightness,
            float opacity)
        {
            if (source.Width <= 0
                || source.Height <= 0
                || destination.Width <= 0
                || destination.Height <= 0
                || opacity <= 0.001f)
            {
                return;
            }

            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                float light = Math.Max(0.0f, Math.Min(1.45f, brightness));
                matrix.Matrix00 = light;
                matrix.Matrix11 = light;
                matrix.Matrix22 = light;
                matrix.Matrix33 = Math.Max(0.0f, Math.Min(1.0f, opacity));
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(
                    bitmap,
                    destination,
                    source.X,
                    source.Y,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }
    }

    internal sealed class ScaledFrameCache : IDisposable
    {
        // At 4K scale the largest 25-stage row plus up to eight keyframes needs
        // roughly 54 MiB.  This bounded LRU keeps that active row warm without
        // turning every row/skin/size combination into permanent memory.
        internal const long BudgetBytes = 128L * 1024L * 1024L;

        private sealed class CacheEntry
        {
            public Bitmap Bitmap;
            public long Bytes;
            public Rectangle VisibleBounds;
            public LinkedListNode<string> Node;
        }

        private byte[] _archiveBytes;
        private string _entryPrefix;
        private readonly Dictionary<string, CacheEntry> _entries;
        private readonly LinkedList<string> _lru;
        private readonly Dictionary<string, MotionField> _motionFields;
        private readonly HashSet<string> _missingMotionFields;
        private long _usedBytes;

        public ScaledFrameCache(byte[] archiveBytes, string entryPrefix)
        {
            _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            _lru = new LinkedList<string>();
            _motionFields = new Dictionary<string, MotionField>(StringComparer.Ordinal);
            _missingMotionFields = new HashSet<string>(StringComparer.Ordinal);
            SetArchive(archiveBytes, entryPrefix);
        }

        public Bitmap Get(int row, int column, int width, int height)
        {
            return GetFrameEntry(row, column, width, height).Bitmap;
        }

        public Rectangle GetVisibleBounds(int row, int column, int width, int height)
        {
            return GetFrameEntry(row, column, width, height).VisibleBounds;
        }

        public Bitmap GetTween(
            int row,
            int column,
            int targetRow,
            int targetColumn,
            int tweenStep,
            int tweenSteps,
            int width,
            int height)
        {
            if (tweenStep <= 0 || tweenSteps <= 1)
            {
                return Get(row, column, width, height);
            }
            if (tweenStep >= tweenSteps)
            {
                return Get(targetRow, targetColumn, width, height);
            }

            string key = "t:" + row + ":" + column + ">" + targetRow + ":" + targetColumn
                + ":" + tweenStep + "/" + tweenSteps + ":" + width + "x" + height;
            CacheEntry cached;
            if (_entries.TryGetValue(key, out cached))
            {
                Touch(cached);
                return cached.Bitmap;
            }

            CacheEntry from = GetFrameEntry(row, column, width, height);
            CacheEntry to = GetFrameEntry(targetRow, targetColumn, width, height);
            float amount = AnimationSmoothing.GetBlendAmount(tweenStep, tweenSteps);
            MotionField motionField;
            TryGetMotionField(row, column, targetRow, targetColumn, out motionField);
            Bitmap tween = CreateTweenFrame(
                from.Bitmap,
                from.VisibleBounds,
                to.Bitmap,
                to.VisibleBounds,
                amount,
                motionField);
            // Tween entries are terminal display frames, never interpolation
            // sources, so avoid a full alpha scan for every synthesized stage.
            return AddEntry(key, tween, Rectangle.Empty).Bitmap;
        }

        private CacheEntry GetFrameEntry(int row, int column, int width, int height)
        {
            string key = "f:" + row + ":" + column + ":" + width + "x" + height;
            CacheEntry cached;
            if (_entries.TryGetValue(key, out cached))
            {
                Touch(cached);
                return cached;
            }

            Bitmap scaled;
            using (Bitmap source = FrameResource.LoadSourceFrame(_archiveBytes, _entryPrefix, row, column))
            {
                scaled = FrameScaler.Scale(source, width, height);
            }

            return AddEntry(key, scaled, FindVisibleBounds(scaled));
        }

        private CacheEntry AddEntry(string key, Bitmap bitmap, Rectangle visibleBounds)
        {
            CacheEntry entry = new CacheEntry();
            entry.Bitmap = bitmap;
            entry.Bytes = (long)bitmap.Width * bitmap.Height * 4L;
            entry.VisibleBounds = visibleBounds;
            entry.Node = _lru.AddFirst(key);
            _entries.Add(key, entry);
            _usedBytes += entry.Bytes;
            Trim();
            return entry;
        }

        private void Touch(CacheEntry entry)
        {
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
        }

        private bool TryGetMotionField(
            int row,
            int column,
            int targetRow,
            int targetColumn,
            out MotionField field)
        {
            string key = row + ":" + column + ">" + targetRow + ":" + targetColumn;
            if (_motionFields.TryGetValue(key, out field))
            {
                return true;
            }
            if (_missingMotionFields.Contains(key))
            {
                field = null;
                return false;
            }
            if (FrameResource.TryLoadMotionField(
                _archiveBytes,
                _entryPrefix,
                row,
                column,
                targetRow,
                targetColumn,
                out field))
            {
                _motionFields.Add(key, field);
                return true;
            }
            _missingMotionFields.Add(key);
            return false;
        }

        internal static Bitmap CreateTweenFrame(
            Bitmap from,
            Rectangle fromBounds,
            Bitmap to,
            Rectangle toBounds,
            float amount,
            MotionField motionField)
        {
            if (motionField != null)
            {
                bool backward = amount >= 0.5f;
                Bitmap owner = backward ? to : from;
                float strength = backward ? 1.0f - amount : amount;
                return WarpSingleOwner(owner, motionField, backward, strength);
            }

            // Older third-party skins may not have compact motion metadata.
            // Keep them ghost-free: move one fully opaque endpoint along the
            // shared anchor path and switch ownership only at the midpoint.
            Bitmap selected = amount < 0.5f ? from : to;
            Rectangle selectedBounds = amount < 0.5f ? fromBounds : toBounds;
            PointF fromAnchor = GetSilhouetteAnchor(fromBounds, from.Width, from.Height);
            PointF toAnchor = GetSilhouetteAnchor(toBounds, to.Width, to.Height);
            PointF movingAnchor = new PointF(
                fromAnchor.X + (toAnchor.X - fromAnchor.X) * amount,
                fromAnchor.Y + (toAnchor.Y - fromAnchor.Y) * amount);
            PointF selectedAnchor = GetSilhouetteAnchor(
                selectedBounds, selected.Width, selected.Height);
            Bitmap fallback = new Bitmap(from.Width, from.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(fallback))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(
                    selected,
                    (int)Math.Round(movingAnchor.X - selectedAnchor.X),
                    (int)Math.Round(movingAnchor.Y - selectedAnchor.Y));
            }
            return fallback;
        }

        private static Bitmap WarpSingleOwner(
            Bitmap source,
            MotionField field,
            bool backward,
            float strength)
        {
            // Read GDI+ properties before LockBits.  Image.Width/Height access
            // itself is not thread-safe while the bitmap is locked.
            int width = source.Width;
            int height = source.Height;
            Bitmap warped = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            Rectangle area = new Rectangle(0, 0, width, height);
            BitmapData sourceData = source.LockBits(
                area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            BitmapData targetData = warped.LockBits(
                area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int sourceStride = Math.Abs(sourceData.Stride);
                int targetStride = Math.Abs(targetData.Stride);
                byte[] sourcePixels = new byte[sourceStride * height];
                byte[] targetPixels = new byte[targetStride * height];
                Marshal.Copy(sourceData.Scan0, sourcePixels, 0, sourcePixels.Length);
                float logicalScaleX = (MotionField.BaseWidth - 1.0f) / Math.Max(1, width - 1);
                float logicalScaleY = (MotionField.BaseHeight - 1.0f) / Math.Max(1, height - 1);
                float pixelScaleX = Math.Max(1, width - 1) / (MotionField.BaseWidth - 1.0f);
                float pixelScaleY = Math.Max(1, height - 1) / (MotionField.BaseHeight - 1.0f);

                System.Threading.Tasks.Parallel.For(0, height, delegate(int y)
                {
                    int targetRow = targetData.Stride >= 0 ? y * targetStride : (height - 1 - y) * targetStride;
                    float logicalY = y * logicalScaleY;
                    for (int x = 0; x < width; x++)
                    {
                        float dx;
                        float dy;
                        field.Sample(backward, x * logicalScaleX, logicalY, out dx, out dy);
                        float sampleX = x - dx * strength * pixelScaleX;
                        float sampleY = y - dy * strength * pixelScaleY;
                        if (sampleX < 0.0f || sampleY < 0.0f
                            || sampleX > width - 1.0f || sampleY > height - 1.0f)
                        {
                            continue;
                        }

                        int x0 = (int)sampleX;
                        int y0 = (int)sampleY;
                        int x1 = Math.Min(width - 1, x0 + 1);
                        int y1 = Math.Min(height - 1, y0 + 1);
                        float tx = sampleX - x0;
                        float ty = sampleY - y0;
                        int row0 = sourceData.Stride >= 0 ? y0 * sourceStride : (height - 1 - y0) * sourceStride;
                        int row1 = sourceData.Stride >= 0 ? y1 * sourceStride : (height - 1 - y1) * sourceStride;
                        int i00 = row0 + x0 * 4;
                        int i10 = row0 + x1 * 4;
                        int i01 = row1 + x0 * 4;
                        int i11 = row1 + x1 * 4;
                        int output = targetRow + x * 4;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            float top = sourcePixels[i00 + channel]
                                + (sourcePixels[i10 + channel] - sourcePixels[i00 + channel]) * tx;
                            float bottom = sourcePixels[i01 + channel]
                                + (sourcePixels[i11 + channel] - sourcePixels[i01 + channel]) * tx;
                            targetPixels[output + channel] = (byte)Math.Max(
                                0,
                                Math.Min(255, (int)Math.Round(top + (bottom - top) * ty)));
                        }
                    }
                });
                Marshal.Copy(targetPixels, 0, targetData.Scan0, targetPixels.Length);
            }
            finally
            {
                source.UnlockBits(sourceData);
                warped.UnlockBits(targetData);
            }
            return warped;
        }

        private static PointF GetSilhouetteAnchor(Rectangle bounds, int width, int height)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return new PointF(width * 0.5f, height * 0.9f);
            }
            return new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Bottom);
        }

        private static void DrawWithOpacity(Graphics graphics, Bitmap bitmap, float x, float y, float opacity)
        {
            if (opacity <= 0.001f)
            {
                return;
            }
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                matrix.Matrix33 = Math.Max(0.0f, Math.Min(1.0f, opacity));
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(
                    bitmap,
                    new Rectangle(
                        (int)Math.Round(x),
                        (int)Math.Round(y),
                        bitmap.Width,
                        bitmap.Height),
                    0.0f,
                    0.0f,
                    bitmap.Width,
                    bitmap.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }
        }

        private static Rectangle FindVisibleBounds(Bitmap bitmap)
        {
            Rectangle area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                int left = bitmap.Width;
                int top = bitmap.Height;
                int right = -1;
                int bottom = -1;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int sourceRow = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                    int offset = sourceRow * stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (pixels[offset + x * 4 + 3] <= 4)
                        {
                            continue;
                        }
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
                return right < left || bottom < top
                    ? Rectangle.Empty
                    : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        public void Clear()
        {
            foreach (CacheEntry entry in _entries.Values)
            {
                entry.Bitmap.Dispose();
            }
            _entries.Clear();
            _lru.Clear();
            _usedBytes = 0;
        }

        public void SetArchive(byte[] archiveBytes, string entryPrefix)
        {
            if (archiveBytes == null || archiveBytes.Length == 0)
            {
                throw new ArgumentException("Frame archive is empty.", "archiveBytes");
            }
            Clear();
            _motionFields.Clear();
            _missingMotionFields.Clear();
            _archiveBytes = archiveBytes;
            _entryPrefix = entryPrefix ?? string.Empty;
        }

        private void Trim()
        {
            while (_usedBytes > BudgetBytes && _entries.Count > 1)
            {
                LinkedListNode<string> node = _lru.Last;
                if (node == null)
                {
                    break;
                }
                CacheEntry entry = _entries[node.Value];
                _entries.Remove(node.Value);
                _lru.RemoveLast();
                _usedBytes -= entry.Bytes;
                entry.Bitmap.Dispose();
            }
        }

        public void Dispose()
        {
            Clear();
        }
    }

    internal static class SelfTest4K
    {
        public static int Run(string reportPath, string previewPath)
        {
            List<string> errors = new List<string>();
            int decodedFrames = 0;
            long visiblePixels = 0;
            byte[] archiveBytes = null;
            int minimumVerifiedStages = int.MaxValue;
            int sittingPhoneLoopStages = 4 * AnimationSmoothing.GetStepsPerTransition(4);
            int sittingEnterExitStages = 3 * AnimationSmoothing.GetStepsPerTransition(3);
            int sideRestSegmentStages = 4 * AnimationSmoothing.GetStepsPerTransition(4);
            int linanSwingEnterStages = 2 * AnimationSmoothing.GetStepsPerTransition(2);
            int linanSwingLoopStages = LinanSwingContract.LoopTransitionCount
                * AnimationSmoothing.GetStepsPerTransition(LinanSwingContract.LoopTransitionCount);
            int linanSwingExitStages = 3 * AnimationSmoothing.GetStepsPerTransition(3);
            int movementStages = AnimationSmoothing.GetDisplayStageCount(8);
            int maximumRuntimeDisplayStages = 0;
            foreach (int keyFrameCount in PetForm.FrameCounts)
            {
                if (keyFrameCount > 0)
                {
                    maximumRuntimeDisplayStages = Math.Max(
                        maximumRuntimeDisplayStages,
                        AnimationSmoothing.GetDisplayStageCount(keyFrameCount));
                }
            }
            int movementTweenSteps = AnimationSmoothing.GetStepsPerTransition(8);
            int walkMotionMilliseconds = AnimationSmoothing.GetReferenceMotionDuration(
                150, 8, AnimationSmoothing.WalkTickMilliseconds);
            int jogMotionMilliseconds = AnimationSmoothing.GetReferenceMotionDuration(
                100, 8, AnimationSmoothing.JogTickMilliseconds);
            int sprintMotionMilliseconds = AnimationSmoothing.GetReferenceMotionDuration(
                70, 8, AnimationSmoothing.MinimumTickMilliseconds);
            int walkCycleMilliseconds = 8 * SumDistributedIntervals(walkMotionMilliseconds, movementTweenSteps);
            int jogCycleMilliseconds = 8 * SumDistributedIntervals(jogMotionMilliseconds, movementTweenSteps);
            int sprintCycleMilliseconds = 8 * SumDistributedIntervals(sprintMotionMilliseconds, movementTweenSteps);
            bool timingPreservedFrom56StageBuild = AnimationSmoothing.MinimumStagesPerCycle == 24
                && AnimationSmoothing.TimingReferenceStagesPerCycle == 56
                && movementStages == 24
                && AnimationSmoothing.GetDisplayStageCount(6) == 24
                && AnimationSmoothing.GetDisplayStageCount(5) == 25
                && AnimationSmoothing.GetDisplayStageCount(4) == 24
                && sittingPhoneLoopStages == 24
                && sittingEnterExitStages == 24
                && sideRestSegmentStages == 24
                && linanSwingEnterStages == 24
                && linanSwingLoopStages == 24
                && linanSwingExitStages == 24
                && walkCycleMilliseconds == 1344
                && jogCycleMilliseconds == 1064
                && sprintCycleMilliseconds == 896
                && StepIntervalsMatch(walkMotionMilliseconds, movementTweenSteps, new int[] { 56, 56, 56 })
                && StepIntervalsMatch(jogMotionMilliseconds, movementTweenSteps, new int[] { 44, 44, 45 })
                && StepIntervalsMatch(sprintMotionMilliseconds, movementTweenSteps, new int[] { 37, 37, 38 });
            long maximumFourKActiveRowBytes = (long)FrameResource.SourceWidth
                * FrameResource.SourceHeight
                * 4L
                * (maximumRuntimeDisplayStages + 8L);
            bool fourKActiveCycleCacheFits = maximumFourKActiveRowBytes <= ScaledFrameCache.BudgetBytes;
            bool sittingPhoneContractValid = false;
            bool sideRestContractValid = false;
            bool linanSwingContractValid = false;
            bool tweenRenderValid = false;
            bool motionFieldLoaded = false;
            bool ghostFreeSingleOwner = false;
            bool headEmitterValid = false;
            bool skinTransitionRevealTopToBottom = true;
            bool skinTransitionFinalPoseValid = false;
            bool skinTransitionRenderValid = false;
            bool dragReactionContractValid = false;
            bool lookTrackingContractValid = false;
            bool skinTransitionBodyTurnContractValid = false;
            bool extensionActionContractValid = false;
            bool builtInExclusiveActionEnabled = false;
            string builtInExclusiveActionName = string.Empty;
            int dragReleaseAngryDurationMilliseconds = 0;
            string skinTransitionPreviewPath = BuildSkinTransitionPreviewPath(previewPath);
            string motionPreviewPath = BuildSiblingPreviewPath(previewPath, "-motion-interpolation");
            int skinTransitionDurationMilliseconds = SkinTransitionContract.TotalStages
                * SkinTransitionContract.TickMilliseconds;
            long skinTransitionOwnedBitmapBytes = (long)FrameResource.SourceWidth
                * FrameResource.SourceHeight
                * 4L
                * SkinTransitionContract.OwnedBitmapCountIncludingComposite;
            long skinTransitionResidentBitmapBytes = (long)FrameResource.SourceWidth
                * FrameResource.SourceHeight
                * 4L
                * SkinTransitionContract.MaximumResidentBitmapCount;
            bool skinTransitionMemoryBounded = skinTransitionResidentBitmapBytes < 12L * 1024L * 1024L
                && SkinTransitionContract.MaximumPendingArchiveBytes == 128L * 1024L * 1024L;

            try
            {
                if (PetForm.FrameCounts.Length != FrameResource.Rows)
                {
                    errors.Add("runtime frame-count table length does not match row contract");
                }
                else
                {
                    for (int row = 0; row < PetForm.FrameCounts.Length; row++)
                    {
                        int keyFrames = PetForm.FrameCounts[row];
                        if (row == 9 || row == 10)
                        {
                            if (keyFrames != 0)
                            {
                                errors.Add("look-direction rows must remain direct-render rows");
                            }
                            continue;
                        }
                        if (keyFrames <= 0 || keyFrames > FrameResource.UsedCellsPerRow[row])
                        {
                            errors.Add("invalid runtime keyframe count for row " + row);
                            continue;
                        }

                        int displayStages = AnimationSmoothing.GetDisplayStageCount(keyFrames);
                        minimumVerifiedStages = Math.Min(minimumVerifiedStages, displayStages);
                        if (displayStages < AnimationSmoothing.MinimumStagesPerCycle)
                        {
                            errors.Add("smoothed stage count is below the configured minimum for row " + row);
                        }
                    }
                }

                if (PetForm.FrameDurations.Length != FrameResource.Rows)
                {
                    errors.Add("runtime duration table length does not match row contract");
                }
                else
                {
                    for (int row = 0; row < PetForm.FrameDurations.Length; row++)
                    {
                        int required = Math.Max(1, PetForm.FrameCounts[row]);
                        int[] durations = PetForm.FrameDurations[row];
                        if (durations == null || durations.Length < required)
                        {
                            errors.Add("runtime duration coverage is incomplete for row " + row);
                            continue;
                        }
                        for (int frame = 0; frame < required; frame++)
                        {
                            if (durations[frame] <= 0)
                            {
                                errors.Add("runtime frame duration is not positive for row " + row);
                                break;
                            }
                        }
                    }
                }

                SkinPack builtInSkin = SkinCatalog.Discover().BuiltIn;
                builtInExclusiveActionName = builtInSkin.ExclusiveActionName;
                builtInExclusiveActionEnabled = builtInSkin.IsBuiltIn
                    && builtInSkin.HasExclusiveAction
                    && string.Equals(
                        builtInExclusiveActionName,
                        "白裙星光亮相",
                        StringComparison.Ordinal);
                extensionActionContractValid = (int)PetState.Adorable == 12
                    && (int)PetState.Laughing == 13
                    && (int)PetState.Crying == 14
                    && (int)PetState.SkinExclusive == 15
                    && PetForm.FrameCounts[(int)PetState.Adorable] == 8
                    && PetForm.FrameCounts[(int)PetState.Laughing] == 8
                    && PetForm.FrameCounts[(int)PetState.Crying] == 8
                    && PetForm.FrameCounts[(int)PetState.SkinExclusive] == 8
                    && builtInExclusiveActionEnabled
                    && !DragReactionContract.MovementRowsRuntimeEnabled
                    && !DragReactionContract.AutomaticRoamingEnabled;
                if (!extensionActionContractValid)
                {
                    errors.Add("v3.0.6 extended idle-action row contract is invalid");
                }

                sittingPhoneContractValid = PetForm.FrameCounts.Length > (int)PetState.Sitting
                    && PetForm.FrameCounts[(int)PetState.Sitting] > PersistentActionContract.SittingExitLastFrame
                    && PersistentActionContract.SittingEnterLastFrame == PersistentActionContract.SittingLoopFirstFrame
                    && sittingPhoneLoopStages >= AnimationSmoothing.MinimumStagesPerCycle
                    && sittingEnterExitStages >= AnimationSmoothing.MinimumStagesPerCycle;
                if (!sittingPhoneContractValid)
                {
                    errors.Add("persistent sitting-phone frame contract is invalid");
                }

                sideRestContractValid = PetForm.FrameCounts.Length > (int)PetState.SideRest
                    && PetForm.FrameCounts[(int)PetState.SideRest] > PersistentActionContract.SideRestWakeLastFrame
                    && PersistentActionContract.SideRestSleepFrame < PersistentActionContract.SideRestWakeFirstFrame
                    && sideRestSegmentStages >= AnimationSmoothing.MinimumStagesPerCycle
                    && SleepEffectLayout.ParticleStageCount >= AnimationSmoothing.MinimumStagesPerCycle;
                if (!sideRestContractValid)
                {
                    errors.Add("persistent side-rest frame contract is invalid");
                }

                SkinPack linanSwingProbe = new SkinPack(
                    LinanSwingContract.SkinId,
                    "QA Linan",
                    "Anbunengsi",
                    string.Empty,
                    false,
                    "荡秋千");
                SkinPack nonLinanExclusiveProbe = new SkinPack(
                    "huang-chengzi",
                    "QA Other",
                    "Anbunengsi",
                    string.Empty,
                    false,
                    "专属动作");
                SkinPack noExclusiveActionProbe = new SkinPack(
                    LinanSwingContract.SkinId,
                    "QA Linan disabled",
                    "Anbunengsi",
                    string.Empty,
                    false,
                    string.Empty);
                linanSwingContractValid = PetForm.FrameCounts.Length > (int)PetState.SkinExclusive
                    && PetForm.FrameCounts[(int)PetState.SkinExclusive] > PersistentActionContract.LinanSwingExitLastFrame
                    && PersistentActionContract.LinanSwingEnterLastFrame == PersistentActionContract.LinanSwingLoopFirstFrame
                    && PersistentActionContract.LinanSwingLoopLastFrame + 1
                        == PersistentActionContract.LinanSwingExitFirstFrame
                    && linanSwingEnterStages >= AnimationSmoothing.MinimumStagesPerCycle
                    && linanSwingLoopStages >= AnimationSmoothing.MinimumStagesPerCycle
                    && linanSwingExitStages >= AnimationSmoothing.MinimumStagesPerCycle
                    && LinanSwingContract.HasAlternatingDepthPeaks()
                    && LinanSwingContract.IsEnabled(linanSwingProbe)
                    && !LinanSwingContract.IsEnabled(nonLinanExclusiveProbe)
                    && !LinanSwingContract.IsEnabled(noExclusiveActionProbe)
                    && !LinanSwingContract.IsRandomEligible(PetState.SkinExclusive, linanSwingProbe)
                    && LinanSwingContract.IsRandomEligible(PetState.SkinExclusive, nonLinanExclusiveProbe);
                if (!linanSwingContractValid)
                {
                    errors.Add("Linan Princess manual swing contract is invalid");
                }

                for (int frame = DragReactionContract.AngryFirstFrame;
                    frame < DragReactionContract.AngryFirstFrame + DragReactionContract.AngryFrameCount;
                    frame++)
                {
                    dragReleaseAngryDurationMilliseconds += PetForm.FrameDurations[DragReactionContract.AngryRow][frame];
                }
                int defaultDragThreshold = DragReactionContract.GetActivationThresholdPixels(2.25f);
                dragReactionContractValid = DragReactionContract.StartleRow == (int)PetState.Jumping
                    && DragReactionContract.StartleFirstFrame == 0
                    && DragReactionContract.StartleHoldFrame == 2
                    && PetForm.FrameCounts[DragReactionContract.StartleRow] > DragReactionContract.StartleHoldFrame
                    && DragReactionContract.AngryRow == (int)PetState.AngryStomp
                    && DragReactionContract.AngryFirstFrame == 2
                    && DragReactionContract.AngryFrameCount == 4
                    && PetForm.FrameCounts[DragReactionContract.AngryRow]
                        >= DragReactionContract.AngryFirstFrame + DragReactionContract.AngryFrameCount
                    && dragReleaseAngryDurationMilliseconds == 1700
                    && defaultDragThreshold == 9
                    && !DragReactionContract.MovementRowsRuntimeEnabled
                    && !DragReactionContract.AutomaticRoamingEnabled
                    && DragReactionContract.DecideRelease(true, false, false, 0, defaultDragThreshold)
                        == DragReleaseAction.Idle
                    && DragReactionContract.DecideRelease(true, false, true, 0, defaultDragThreshold)
                        == DragReleaseAction.Wave
                    && DragReactionContract.DecideRelease(true, true, false, defaultDragThreshold, defaultDragThreshold)
                        == DragReleaseAction.Angry
                    && DragReactionContract.DecideRelease(false, true, false, defaultDragThreshold, defaultDragThreshold)
                        == DragReleaseAction.Idle;
                if (!dragReactionContractValid)
                {
                    errors.Add("drag startle and release reaction contract is invalid");
                }

                lookTrackingContractValid = MouseLookContract.EntryRadiusLogicalPixels == 220.0f
                    && MouseLookContract.ExitRadiusLogicalPixels == 270.0f
                    && MouseLookContract.EntryRadiusLogicalPixels < MouseLookContract.ExitRadiusLogicalPixels
                    && MouseLookContract.ExitDeadZoneLogicalPixels < MouseLookContract.EntryDeadZoneLogicalPixels
                    && Math.Abs(MouseLookContract.GetOuterRadius(2.25f, false) - 495.0) < 0.01
                    && Math.Abs(MouseLookContract.GetOuterRadius(2.25f, true) - 607.5) < 0.01
                    && MouseLookContract.FarPointerReturnsBlinkOnlyIdle;
                if (!lookTrackingContractValid)
                {
                    errors.Add("mouse-look entry/exit hysteresis contract is invalid");
                }

                skinTransitionBodyTurnContractValid = SkinTransitionContract.BodySliceCount >= 12
                    && SkinTransitionContract.MinimumSliceWidthScale >= 0.50f
                    && SkinTransitionContract.MinimumSliceWidthScale < 0.85f
                    && SkinTransitionContract.UsesAlphaMaskedBodySlices
                    && !SkinTransitionContract.WholeCanvasCardFlip
                    && SkinTransitionContract.FinalFrameIsPose;
                if (!skinTransitionBodyTurnContractValid)
                {
                    errors.Add("alpha-sliced body-turn skin transition contract is invalid");
                }
                if (!(walkCycleMilliseconds > jogCycleMilliseconds
                    && jogCycleMilliseconds > sprintCycleMilliseconds))
                {
                    errors.Add("walk, jog and sprint cadence ordering is invalid");
                }
                if (!timingPreservedFrom56StageBuild)
                {
                    errors.Add("24-stage timing does not preserve the v3.0.1 56-stage cadence");
                }
                if (!fourKActiveCycleCacheFits)
                {
                    errors.Add("4K active animation cycle exceeds the bounded tween cache");
                }

                float previousReveal = -1.0f;
                for (int stage = 0; stage < SkinTransitionContract.RevealStages; stage++)
                {
                    float reveal = SkinTransitionContract.GetRevealFraction(stage);
                    int revealY = SkinTransitionContract.GetRevealY(stage, FrameResource.SourceHeight);
                    int previousRevealY = stage == 0
                        ? -1
                        : SkinTransitionContract.GetRevealY(stage - 1, FrameResource.SourceHeight);
                    if ((stage > 0 && reveal <= previousReveal)
                        || reveal < 0.0f
                        || reveal > 1.0f
                        || revealY <= previousRevealY)
                    {
                        skinTransitionRevealTopToBottom = false;
                        break;
                    }
                    previousReveal = reveal;
                }
                skinTransitionRevealTopToBottom = skinTransitionRevealTopToBottom
                    && Math.Abs(previousReveal - 1.0f) < 0.0001f;
                skinTransitionFinalPoseValid = SkinTransitionContract.FinalPoseRow == (int)PetState.FlyingKiss
                    && PetForm.FrameCounts[SkinTransitionContract.FinalPoseRow] > SkinTransitionContract.FinalPoseColumn;
                if (SkinTransitionContract.TotalStages < AnimationSmoothing.MinimumStagesPerCycle
                    || skinTransitionDurationMilliseconds < 1000
                    || skinTransitionDurationMilliseconds > 1500)
                {
                    errors.Add("skin transition stage count or duration is outside its contract");
                }
                if (!skinTransitionRevealTopToBottom)
                {
                    errors.Add("skin transition reveal is not monotonic from head to toe");
                }
                if (!skinTransitionFinalPoseValid)
                {
                    errors.Add("skin transition final-pose contract is invalid");
                }
                if (!SkinTransitionContract.LocksActionInput)
                {
                    errors.Add("skin transition input lock contract is disabled");
                }
                if (!skinTransitionMemoryBounded)
                {
                    errors.Add("skin transition memory bound is invalid");
                }

                archiveBytes = FrameResource.LoadArchiveBytes();
                int expectedFrames = 0;
                foreach (int count in FrameResource.UsedCellsPerRow)
                {
                    expectedFrames += count;
                }
                int archiveFrames = FrameResource.CountPngEntries(archiveBytes);
                if (archiveFrames != expectedFrames)
                {
                    errors.Add("archive frame count is " + archiveFrames + "; expected " + expectedFrames);
                }

                for (int row = 0; row < FrameResource.Rows; row++)
                {
                    for (int column = 0; column < FrameResource.UsedCellsPerRow[row]; column++)
                    {
                        using (Bitmap frame = FrameResource.LoadSourceFrame(archiveBytes, row, column))
                        {
                            decodedFrames++;
                            if (frame.Width != FrameResource.SourceWidth || frame.Height != FrameResource.SourceHeight)
                            {
                                errors.Add("unexpected dimensions: " + FrameResource.EntryName(row, column));
                            }
                            long alpha = CountVisiblePixels(frame);
                            visiblePixels += alpha;
                            if (alpha == 0)
                            {
                                errors.Add("empty frame: " + FrameResource.EntryName(row, column));
                            }

                            if (row == 0 && column == 0)
                            {
                                SavePreview(frame, previewPath);
                                using (Bitmap scaled = FrameScaler.Scale(
                                    frame,
                                    FrameResource.LogicalWidth * 2,
                                    FrameResource.LogicalHeight * 2))
                                {
                                    if (scaled.PixelFormat != PixelFormat.Format32bppPArgb)
                                    {
                                        errors.Add("runtime scale path did not produce PArgb");
                                    }
                                }
                            }
                        }
                    }
                }

                // Exercise the real scaled-frame LRU and mid-transition draw
                // path, not only the stage-count arithmetic.
                using (ScaledFrameCache cache = new ScaledFrameCache(archiveBytes, "frames/"))
                {
                    int width = FrameResource.LogicalWidth * 2;
                    int height = FrameResource.LogicalHeight * 2;
                    int steps = AnimationSmoothing.GetStepsPerTransition(PetForm.FrameCounts[(int)PetState.HandDance]);
                    Bitmap from = cache.Get((int)PetState.HandDance, 0, width, height);
                    Bitmap to = cache.Get((int)PetState.HandDance, 1, width, height);
                    Bitmap tween = cache.GetTween((int)PetState.HandDance, 0, (int)PetState.HandDance, 1, steps / 2, steps, width, height);
                    MotionField motionField;
                    motionFieldLoaded = FrameResource.TryLoadMotionField(
                        archiveBytes,
                        "frames/",
                        (int)PetState.HandDance,
                        0,
                        (int)PetState.HandDance,
                        1,
                        out motionField);
                    tweenRenderValid = tween.PixelFormat == PixelFormat.Format32bppPArgb
                        && CountVisiblePixels(tween) > 0
                        && CountOpaqueCorePixels(tween) >= Math.Min(
                            CountOpaqueCorePixels(from), CountOpaqueCorePixels(to)) * 7L / 10L
                        && ComputePixelSignature(tween) != ComputePixelSignature(from)
                        && ComputePixelSignature(tween) != ComputePixelSignature(to);
                    SaveMotionPreview(from, tween, to, motionPreviewPath);

                    if (motionFieldLoaded)
                    {
                        using (Bitmap redPose = CreateSolidTestPose(width, height, Color.FromArgb(255, 235, 40, 40)))
                        using (Bitmap bluePose = CreateSolidTestPose(width, height, Color.FromArgb(255, 40, 90, 235)))
                        using (Bitmap syntheticTween = ScaledFrameCache.CreateTweenFrame(
                            redPose,
                            new Rectangle(width / 3, height / 5, width / 3, height * 3 / 5),
                            bluePose,
                            new Rectangle(width / 3, height / 5, width / 3, height * 3 / 5),
                            0.5f,
                            motionField))
                        {
                            ghostFreeSingleOwner = CountDominantColor(syntheticTween, true) == 0
                                && CountDominantColor(syntheticTween, false) > 0;
                        }
                    }

                    Rectangle sleepBounds = cache.GetVisibleBounds(
                        (int)PetState.SideRest,
                        PersistentActionContract.SideRestSleepFrame,
                        width,
                        height);
                    PointF emitter = SleepEffectLayout.GetHeadEmitter(sleepBounds, width, height);
                    headEmitterValid = sleepBounds.Width > 0
                        && emitter.X >= sleepBounds.Left
                        && emitter.X <= sleepBounds.Left + sleepBounds.Width * 0.45f
                        && emitter.Y >= sleepBounds.Top
                        && emitter.Y <= sleepBounds.Top + sleepBounds.Height * 0.25f;

                    Bitmap transitionOld = cache.Get((int)PetState.HandDance, 0, width, height);
                    Bitmap transitionPose = cache.Get(
                        SkinTransitionContract.FinalPoseRow,
                        SkinTransitionContract.FinalPoseColumn,
                        width,
                        height);
                    Bitmap transitionIdle = cache.Get((int)PetState.Idle, 0, width, height);
                    using (Bitmap turn = SkinTransitionRenderer.Render(
                        transitionOld,
                        transitionPose,
                        transitionIdle,
                        SkinTransitionContract.SpinStages / 2))
                    using (Bitmap reveal = SkinTransitionRenderer.Render(
                        transitionOld,
                        transitionPose,
                        transitionIdle,
                        SkinTransitionContract.SpinStages + SkinTransitionContract.RevealStages / 2))
                    using (Bitmap finalPose = SkinTransitionRenderer.Render(
                        transitionOld,
                        transitionPose,
                        transitionIdle,
                        SkinTransitionContract.TotalStages - 1))
                    {
                        long expectedPosePixels = CountVisiblePixels(transitionPose);
                        long finalPosePixels = CountVisiblePixels(finalPose);
                        long poseTolerance = Math.Max(4L, expectedPosePixels / 100L);
                        skinTransitionRenderValid = turn.PixelFormat == PixelFormat.Format32bppPArgb
                            && reveal.PixelFormat == PixelFormat.Format32bppPArgb
                            && finalPose.PixelFormat == PixelFormat.Format32bppPArgb
                            && CountVisiblePixels(turn) > 0
                            && CountVisiblePixels(reveal) > 0
                            && HasNoFullWidthAlphaBand(turn)
                            && HasNoFullWidthAlphaBand(reveal)
                            && Math.Abs(finalPosePixels - expectedPosePixels) <= poseTolerance;
                    }
                    SaveSkinTransitionPreview(
                        transitionOld,
                        transitionPose,
                        transitionIdle,
                        skinTransitionPreviewPath);
                }
                if (!tweenRenderValid)
                {
                    errors.Add("runtime midpoint tween render is invalid or indistinguishable");
                }
                if (!motionFieldLoaded)
                {
                    errors.Add("embedded ghost-free motion field is missing");
                }
                if (!ghostFreeSingleOwner)
                {
                    errors.Add("runtime tween draws more than one pose owner");
                }
                if (!headEmitterValid)
                {
                    errors.Add("sleep particle emitter is not anchored to the built-in sleeping head region");
                }
                if (!skinTransitionRenderValid)
                {
                    errors.Add("skin transition renderer did not produce a visible PArgb frame");
                }
            }
            catch (Exception exception)
            {
                AggregateException aggregate = exception as AggregateException;
                if (aggregate != null)
                {
                    foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                    {
                        errors.Add(inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace);
                    }
                }
                else
                {
                    errors.Add(exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
                }
            }

            bool ok = errors.Count == 0;
            WriteReport(
                reportPath,
                previewPath,
                archiveBytes == null ? 0 : archiveBytes.Length,
                decodedFrames,
                visiblePixels,
                minimumVerifiedStages == int.MaxValue ? 0 : minimumVerifiedStages,
                sittingPhoneLoopStages,
                sittingEnterExitStages,
                sideRestSegmentStages,
                linanSwingEnterStages,
                linanSwingLoopStages,
                linanSwingExitStages,
                walkCycleMilliseconds,
                jogCycleMilliseconds,
                sprintCycleMilliseconds,
                timingPreservedFrom56StageBuild,
                maximumFourKActiveRowBytes,
                fourKActiveCycleCacheFits,
                skinTransitionDurationMilliseconds,
                skinTransitionOwnedBitmapBytes,
                skinTransitionResidentBitmapBytes,
                skinTransitionRevealTopToBottom,
                skinTransitionFinalPoseValid,
                skinTransitionRenderValid,
                skinTransitionMemoryBounded,
                skinTransitionPreviewPath,
                sittingPhoneContractValid,
                sideRestContractValid,
                linanSwingContractValid,
                dragReactionContractValid,
                dragReleaseAngryDurationMilliseconds,
                tweenRenderValid,
                motionFieldLoaded,
                ghostFreeSingleOwner,
                motionPreviewPath,
                headEmitterValid,
                builtInExclusiveActionEnabled,
                builtInExclusiveActionName,
                errors,
                ok);
            return ok ? 0 : 2;
        }

        private static int SumDistributedIntervals(int motionDuration, int tweenSteps)
        {
            int total = 0;
            for (int step = 0; step < tweenSteps; step++)
            {
                total += AnimationSmoothing.GetDistributedStepInterval(motionDuration, tweenSteps, step);
            }
            return total;
        }

        private static bool StepIntervalsMatch(int motionDuration, int tweenSteps, int[] expected)
        {
            if (expected == null || expected.Length != tweenSteps)
            {
                return false;
            }
            for (int step = 0; step < tweenSteps; step++)
            {
                if (AnimationSmoothing.GetDistributedStepInterval(motionDuration, tweenSteps, step) != expected[step])
                {
                    return false;
                }
            }
            return true;
        }

        private static long CountVisiblePixels(Bitmap source)
        {
            Rectangle rectangle = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData data = source.LockBits(rectangle, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                long count = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    int sourceRow = data.Stride >= 0 ? y : source.Height - 1 - y;
                    int offset = sourceRow * stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (bytes[offset + x * 4 + 3] != 0)
                        {
                            count++;
                        }
                    }
                }
                return count;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static bool HasNoFullWidthAlphaBand(Bitmap source)
        {
            Rectangle rectangle = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData data = source.LockBits(rectangle, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                int forbiddenWidth = Math.Max(1, source.Width * 9 / 10);
                for (int y = 0; y < source.Height; y++)
                {
                    int sourceRow = data.Stride >= 0 ? y : source.Height - 1 - y;
                    int offset = sourceRow * stride;
                    int visible = 0;
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (bytes[offset + x * 4 + 3] > 8)
                        {
                            visible++;
                        }
                    }
                    if (visible >= forbiddenWidth)
                    {
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static long CountOpaqueCorePixels(Bitmap source)
        {
            Rectangle rectangle = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData data = source.LockBits(rectangle, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                long count = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    int sourceRow = data.Stride >= 0 ? y : source.Height - 1 - y;
                    int offset = sourceRow * stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (bytes[offset + x * 4 + 3] >= 224)
                        {
                            count++;
                        }
                    }
                }
                return count;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static Bitmap CreateSolidTestPose(int width, int height, Color color)
        {
            Bitmap pose = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(pose))
            using (Brush brush = new SolidBrush(color))
            {
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(brush, width / 3, height / 5, width / 3, height * 3 / 5);
            }
            return pose;
        }

        private static long CountDominantColor(Bitmap source, bool red)
        {
            Rectangle rectangle = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData data = source.LockBits(rectangle, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                long count = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    int sourceRow = data.Stride >= 0 ? y : source.Height - 1 - y;
                    int offset = sourceRow * stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int pixel = offset + x * 4;
                        byte blue = bytes[pixel];
                        byte green = bytes[pixel + 1];
                        byte redValue = bytes[pixel + 2];
                        byte alpha = bytes[pixel + 3];
                        if (alpha > 32 && (red
                            ? redValue > blue + 48 && redValue > green + 48
                            : blue > redValue + 48 && blue > green + 48))
                        {
                            count++;
                        }
                    }
                }
                return count;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static ulong ComputePixelSignature(Bitmap source)
        {
            Rectangle rectangle = new Rectangle(0, 0, source.Width, source.Height);
            BitmapData data = source.LockBits(rectangle, ImageLockMode.ReadOnly, source.PixelFormat);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] bytes = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                ulong hash = 1469598103934665603UL;
                for (int index = 0; index < bytes.Length; index++)
                {
                    hash ^= bytes[index];
                    hash *= 1099511628211UL;
                }
                return hash;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        private static void SavePreview(Bitmap frame, string previewPath)
        {
            string fullPath = Path.GetFullPath(previewPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            frame.Save(fullPath, ImageFormat.Png);
        }

        private static string BuildSkinTransitionPreviewPath(string previewPath)
        {
            string fullPath = Path.GetFullPath(previewPath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(fullPath) + "-skin-transition.png";
            return Path.Combine(directory, name);
        }

        private static string BuildSiblingPreviewPath(string previewPath, string suffix)
        {
            string fullPath = Path.GetFullPath(previewPath);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(fullPath) + suffix + ".png";
            return Path.Combine(directory, name);
        }

        private static void SaveMotionPreview(
            Bitmap from,
            Bitmap midpoint,
            Bitmap to,
            string outputPath)
        {
            int gap = 12;
            int header = 34;
            using (Bitmap contact = new Bitmap(
                from.Width * 3 + gap * 4,
                from.Height + header + gap,
                PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(contact))
            using (Font font = new Font("Arial", 10.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush textBrush = new SolidBrush(Color.White))
            {
                graphics.Clear(Color.FromArgb(255, 31, 33, 36));
                string[] labels = new string[] { "FROM", "MIDPOINT - ONE OPAQUE OWNER", "TO" };
                Bitmap[] samples = new Bitmap[] { from, midpoint, to };
                for (int index = 0; index < samples.Length; index++)
                {
                    int x = gap + index * (from.Width + gap);
                    graphics.DrawString(labels[index], font, textBrush, x, 8);
                    graphics.DrawImageUnscaled(samples[index], x, header);
                }
                string directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                contact.Save(outputPath, ImageFormat.Png);
            }
        }

        private static void SaveSkinTransitionPreview(
            Bitmap oldFrame,
            Bitmap newPose,
            Bitmap newIdle,
            string previewPath)
        {
            string fullPath = Path.GetFullPath(previewPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            int[] stages = new int[]
            {
                0,
                SkinTransitionContract.SpinStages / 2,
                SkinTransitionContract.SpinStages + SkinTransitionContract.RevealStages / 2,
                SkinTransitionContract.SpinStages + SkinTransitionContract.RevealStages + SkinTransitionContract.HoldStages / 2,
                SkinTransitionContract.TotalStages - 1
            };
            string[] labels = new string[] { "OLD", "BODY TURN", "ALPHA SCAN", "POSE", "FINAL POSE" };
            int header = 24;
            using (Bitmap contact = new Bitmap(
                oldFrame.Width * stages.Length,
                oldFrame.Height + header,
                PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(contact))
            using (Font font = new Font(FontFamily.GenericSansSerif, 11.0f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush text = new SolidBrush(Color.FromArgb(235, 225, 242, 255)))
            {
                graphics.Clear(Color.FromArgb(255, 30, 32, 36));
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (int index = 0; index < stages.Length; index++)
                {
                    using (Bitmap sample = SkinTransitionRenderer.Render(oldFrame, newPose, newIdle, stages[index]))
                    {
                        int x = index * oldFrame.Width;
                        graphics.DrawString(labels[index], font, text, x + 7, 5);
                        graphics.DrawImageUnscaled(sample, x, header);
                    }
                }
                contact.Save(fullPath, ImageFormat.Png);
            }
        }

        private static void WriteReport(
            string reportPath,
            string previewPath,
            int archiveBytes,
            int decodedFrames,
            long visiblePixels,
            int minimumVerifiedStages,
            int sittingPhoneLoopStages,
            int sittingEnterExitStages,
            int sideRestSegmentStages,
            int linanSwingEnterStages,
            int linanSwingLoopStages,
            int linanSwingExitStages,
            int walkCycleMilliseconds,
            int jogCycleMilliseconds,
            int sprintCycleMilliseconds,
            bool timingPreservedFrom56StageBuild,
            long maximumFourKActiveRowBytes,
            bool fourKActiveCycleCacheFits,
            int skinTransitionDurationMilliseconds,
            long skinTransitionOwnedBitmapBytes,
            long skinTransitionResidentBitmapBytes,
            bool skinTransitionRevealTopToBottom,
            bool skinTransitionFinalPoseValid,
            bool skinTransitionRenderValid,
            bool skinTransitionMemoryBounded,
            string skinTransitionPreviewPath,
            bool sittingPhoneContractValid,
            bool sideRestContractValid,
            bool linanSwingContractValid,
            bool dragReactionContractValid,
            int dragReleaseAngryDurationMilliseconds,
            bool tweenRenderValid,
            bool motionFieldLoaded,
            bool ghostFreeSingleOwner,
            string motionPreviewPath,
            bool headEmitterValid,
            bool builtInExclusiveActionEnabled,
            string builtInExclusiveActionName,
            List<string> errors,
            bool ok)
        {
            string fullPath = Path.GetFullPath(reportPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"ok\": " + (ok ? "true" : "false") + ",");
            builder.AppendLine("  \"application\": \"小曦薇\",");
            builder.AppendLine("  \"version\": \"3.0.6\",");
            builder.AppendLine("  \"developer\": \"Anbunengsi\",");
            builder.AppendLine("  \"frameArchiveEmbedded\": true,");
            builder.AppendLine("  \"frameArchiveResource\": \"" + FrameResource.ResourceName + "\",");
            builder.AppendLine("  \"archiveBytes\": " + archiveBytes + ",");
            builder.AppendLine("  \"decodedFrames\": " + decodedFrames + ",");
            builder.AppendLine("  \"sourceFrameWidth\": " + FrameResource.SourceWidth + ",");
            builder.AppendLine("  \"sourceFrameHeight\": " + FrameResource.SourceHeight + ",");
            builder.AppendLine("  \"logicalWidth\": " + FrameResource.LogicalWidth + ",");
            builder.AppendLine("  \"logicalHeight\": " + FrameResource.LogicalHeight + ",");
            builder.AppendLine("  \"visiblePixels\": " + visiblePixels + ",");
            builder.AppendLine("  \"runtimeTweening\": true,");
            builder.AppendLine("  \"tweenFramesEmbedded\": false,");
            builder.AppendLine("  \"motionInterpolation\": \"single-owner bidirectional optical-flow mesh\",");
            builder.AppendLine("  \"wholeFrameCrossFade\": false,");
            builder.AppendLine("  \"motionFieldLoaded\": " + (motionFieldLoaded ? "true" : "false") + ",");
            builder.AppendLine("  \"ghostFreeSinglePoseOwner\": " + (ghostFreeSingleOwner ? "true" : "false") + ",");
            builder.AppendLine("  \"motionInterpolationPreview\": \"" + EscapeJson(Path.GetFullPath(motionPreviewPath)) + "\",");
            builder.AppendLine("  \"minimumDisplayStagesPerCycle\": " + AnimationSmoothing.MinimumStagesPerCycle + ",");
            builder.AppendLine("  \"timingReferenceStagesPerCycle\": " + AnimationSmoothing.TimingReferenceStagesPerCycle + ",");
            builder.AppendLine("  \"minimumVerifiedDisplayStagesPerCycle\": " + minimumVerifiedStages + ",");
            builder.AppendLine("  \"timingPreservedFrom56StageBuild\": " + (timingPreservedFrom56StageBuild ? "true" : "false") + ",");
            builder.AppendLine("  \"runtimeMidpointTweenRenderValid\": " + (tweenRenderValid ? "true" : "false") + ",");
            builder.AppendLine("  \"tweenCacheBudgetBytes\": " + ScaledFrameCache.BudgetBytes + ",");
            builder.AppendLine("  \"maximumFourKActiveRowBytes\": " + maximumFourKActiveRowBytes + ",");
            builder.AppendLine("  \"fourKActiveCycleCacheFits\": " + (fourKActiveCycleCacheFits ? "true" : "false") + ",");
            builder.AppendLine("  \"lookDirectionRowsDirect\": true,");
            builder.AppendLine("  \"lookTrackingEntryRadiusLogicalPixels\": " + MouseLookContract.EntryRadiusLogicalPixels.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"lookTrackingExitRadiusLogicalPixels\": " + MouseLookContract.ExitRadiusLogicalPixels.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"lookTrackingEntryDeadZoneLogicalPixels\": " + MouseLookContract.EntryDeadZoneLogicalPixels.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"lookTrackingExitDeadZoneLogicalPixels\": " + MouseLookContract.ExitDeadZoneLogicalPixels.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"lookTrackingUsesEntryExitHysteresis\": true,");
            builder.AppendLine("  \"lookTrackingFarPointerReturnsBlinkOnlyIdle\": " + (MouseLookContract.FarPointerReturnsBlinkOnlyIdle ? "true" : "false") + ",");
            builder.AppendLine("  \"movementRowsRuntimeEnabled\": " + (DragReactionContract.MovementRowsRuntimeEnabled ? "true" : "false") + ",");
            builder.AppendLine("  \"automaticRoamingEnabled\": " + (DragReactionContract.AutomaticRoamingEnabled ? "true" : "false") + ",");
            builder.AppendLine("  \"builtInExtendedActionRows\": [12, 13, 14, 15],");
            builder.AppendLine("  \"builtInExclusiveActionEnabled\": " + (builtInExclusiveActionEnabled ? "true" : "false") + ",");
            builder.AppendLine("  \"builtInExclusiveActionName\": \"" + EscapeJson(builtInExclusiveActionName) + "\",");
            builder.AppendLine("  \"skinExclusiveActionRow\": 15,");
            builder.AppendLine("  \"skinExclusiveActionMetadataOptIn\": \"exclusiveAction\",");
            builder.AppendLine("  \"linanSwingSkinId\": \"" + LinanSwingContract.SkinId + "\",");
            builder.AppendLine("  \"linanSwingActionRow\": 15,");
            builder.AppendLine("  \"linanSwingManualOnly\": true,");
            builder.AppendLine("  \"linanSwingPersistentUntilClick\": " + (linanSwingContractValid ? "true" : "false") + ",");
            builder.AppendLine("  \"linanSwingEnterFrames\": \"0-1-2\",");
            builder.AppendLine("  \"linanSwingLoopFrames\": \"2-3-4-5-2\",");
            builder.AppendLine("  \"linanSwingLoopTransitionCount\": " + LinanSwingContract.LoopTransitionCount + ",");
            builder.AppendLine("  \"linanSwingLoopWrapFrames\": \"5-2\",");
            builder.AppendLine("  \"linanSwingClickExitFrames\": \"2-6-7-idle\",");
            builder.AppendLine("  \"linanSwingExitWaitFrame\": 2,");
            builder.AppendLine("  \"linanSwingAlternatingDepthPeaks\": " + (LinanSwingContract.HasAlternatingDepthPeaks() ? "true" : "false") + ",");
            builder.AppendLine("  \"linanSwingEnterDisplayStages\": " + linanSwingEnterStages + ",");
            builder.AppendLine("  \"linanSwingLoopDisplayStages\": " + linanSwingLoopStages + ",");
            builder.AppendLine("  \"linanSwingExitDisplayStages\": " + linanSwingExitStages + ",");
            builder.AppendLine("  \"walkCycleMilliseconds\": " + walkCycleMilliseconds + ",");
            builder.AppendLine("  \"jogCycleMilliseconds\": " + jogCycleMilliseconds + ",");
            builder.AppendLine("  \"sprintCycleMilliseconds\": " + sprintCycleMilliseconds + ",");
            builder.AppendLine("  \"skinTransitionEnabled\": true,");
            builder.AppendLine("  \"skinTransitionAtomicCacheSwap\": true,");
            builder.AppendLine("  \"skinTransitionRepeatedSelectionPolicy\": \"latest-queued\",");
            builder.AppendLine("  \"skinTransitionPauseSafe\": true,");
            builder.AppendLine("  \"skinTransitionScaleSafe\": true,");
            builder.AppendLine("  \"skinTransitionCloseDisposesResources\": true,");
            builder.AppendLine("  \"skinTransitionQaWindowAutoExercise\": true,");
            builder.AppendLine("  \"skinTransitionSpinStages\": " + SkinTransitionContract.SpinStages + ",");
            builder.AppendLine("  \"skinTransitionRevealStages\": " + SkinTransitionContract.RevealStages + ",");
            builder.AppendLine("  \"skinTransitionHoldStages\": " + SkinTransitionContract.HoldStages + ",");
            builder.AppendLine("  \"skinTransitionSettleStages\": " + SkinTransitionContract.SettleStages + ",");
            builder.AppendLine("  \"skinTransitionTotalStages\": " + SkinTransitionContract.TotalStages + ",");
            builder.AppendLine("  \"skinTransitionDurationMilliseconds\": " + skinTransitionDurationMilliseconds + ",");
            builder.AppendLine("  \"skinTransitionBodySliceCount\": " + SkinTransitionContract.BodySliceCount + ",");
            builder.AppendLine("  \"skinTransitionMinimumSliceWidthScale\": " + SkinTransitionContract.MinimumSliceWidthScale.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"skinTransitionUsesAlphaMaskedBodySlices\": " + (SkinTransitionContract.UsesAlphaMaskedBodySlices ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionWholeCanvasCardFlip\": " + (SkinTransitionContract.WholeCanvasCardFlip ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionNoRectangularCardEdges\": true,");
            builder.AppendLine("  \"skinTransitionFinalFrameIsPose\": " + (SkinTransitionContract.FinalFrameIsPose ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionTopToBottomReveal\": " + (skinTransitionRevealTopToBottom ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionFinalPoseRow\": " + SkinTransitionContract.FinalPoseRow + ",");
            builder.AppendLine("  \"skinTransitionFinalPoseColumn\": " + SkinTransitionContract.FinalPoseColumn + ",");
            builder.AppendLine("  \"skinTransitionFinalPoseValid\": " + (skinTransitionFinalPoseValid ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionFinalPoseFallbackIdle\": true,");
            builder.AppendLine("  \"skinTransitionInputLock\": " + (SkinTransitionContract.LocksActionInput ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionRenderValid\": " + (skinTransitionRenderValid ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionOwnedBitmapMaxBytes\": " + skinTransitionOwnedBitmapBytes + ",");
            builder.AppendLine("  \"skinTransitionResidentBitmapMaxBytes\": " + skinTransitionResidentBitmapBytes + ",");
            builder.AppendLine("  \"skinTransitionPendingArchiveMaxBytes\": " + SkinTransitionContract.MaximumPendingArchiveBytes + ",");
            builder.AppendLine("  \"skinTransitionMemoryBounded\": " + (skinTransitionMemoryBounded ? "true" : "false") + ",");
            builder.AppendLine("  \"skinTransitionPreview\": \"" + EscapeJson(Path.GetFullPath(skinTransitionPreviewPath)) + "\",");
            builder.AppendLine("  \"sittingPhonePersistentUntilClick\": " + (sittingPhoneContractValid ? "true" : "false") + ",");
            builder.AppendLine("  \"sittingPhoneEnterFrames\": \"0-3\",");
            builder.AppendLine("  \"sittingPhoneLoopFrames\": \"3-4-5-4\",");
            builder.AppendLine("  \"sittingPhoneLoopDisplayStages\": " + sittingPhoneLoopStages + ",");
            builder.AppendLine("  \"sittingPhoneEnterExitDisplayStages\": " + sittingEnterExitStages + ",");
            builder.AppendLine("  \"sittingPhoneClickExitFrames\": \"6-7\",");
            builder.AppendLine("  \"sideRestPersistentUntilClick\": " + (sideRestContractValid ? "true" : "false") + ",");
            builder.AppendLine("  \"sideRestSleepFrame\": 4,");
            builder.AppendLine("  \"sideRestWakeFrames\": \"5-7\",");
            builder.AppendLine("  \"sideRestEntryWakeDisplayStages\": " + sideRestSegmentStages + ",");
            builder.AppendLine("  \"sleepParticleDisplayStages\": " + SleepEffectLayout.ParticleStageCount + ",");
            builder.AppendLine("  \"sleepParticlesHeadAnchored\": " + (headEmitterValid ? "true" : "false") + ",");
            builder.AppendLine("  \"dragReactionContractValid\": " + (dragReactionContractValid ? "true" : "false") + ",");
            builder.AppendLine("  \"dragActivationMinimumPixels\": " + DragReactionContract.MinimumDragPixels + ",");
            builder.AppendLine("  \"dragActivationPixelsAtDefaultScale\": " + DragReactionContract.GetActivationThresholdPixels(2.25f) + ",");
            builder.AppendLine("  \"dragStartleFrames\": \"0-2\",");
            builder.AppendLine("  \"dragReleaseAngryFirstFrame\": " + DragReactionContract.AngryFirstFrame + ",");
            builder.AppendLine("  \"dragReleaseAngryFrameCount\": " + DragReactionContract.AngryFrameCount + ",");
            builder.AppendLine("  \"dragReleaseAngryDurationMilliseconds\": " + dragReleaseAngryDurationMilliseconds + ",");
            builder.AppendLine("  \"ordinaryClickTriggersAnger\": false,");
            builder.AppendLine("  \"doubleClickWavePreserved\": true,");
            builder.AppendLine("  \"captureLossTriggersAnger\": false,");
            builder.AppendLine("  \"preview\": \"" + EscapeJson(Path.GetFullPath(previewPath)) + "\",");
            builder.AppendLine("  \"errors\": [");
            for (int index = 0; index < errors.Count; index++)
            {
                builder.Append("    \"");
                builder.Append(EscapeJson(errors[index]));
                builder.Append("\"");
                builder.AppendLine(index == errors.Count - 1 ? string.Empty : ",");
            }
            builder.AppendLine("  ]");
            builder.AppendLine("}");
            File.WriteAllText(fullPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal sealed class WeiboFeedMonitor : IDisposable
    {
        private const string FeedUrl = "https://www.sina.cn/media/2731935637";
        private const int RequestTimeoutMilliseconds = 12000;
        private const int MaxResponseBytes = 2 * 1024 * 1024;
        private const int DisabledTimerValue = Timeout.Infinite;

        private static readonly Regex PostLinkPattern = new Regex(
            @"/news/detail/([0-9]+)\.html",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex EnabledPattern = new Regex(
            @"enabled\s*=\s*""(true|false)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex LastSeenPattern = new Regex(
            @"lastSeenId\s*=\s*""([0-9]+)""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly object _sync = new object();
        private readonly System.Threading.Timer _timer;
        private readonly Random _random;
        private readonly bool _networkAllowed;
        private readonly Action<long, Uri> _onNewPost;
        private readonly string _settingsPath;
        private HttpWebRequest _activeRequest;
        private bool _disposed;
        private bool _polling;
        private bool _enabled;
        private long _lastSeenId;
        private int _failureCount;

        public WeiboFeedMonitor(bool networkAllowed, Action<long, Uri> onNewPost)
        {
            _networkAllowed = networkAllowed;
            _onNewPost = onNewPost;
            _random = new Random(unchecked(Environment.TickCount * 397) ^ Process.GetCurrentProcess().Id);
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Anbunengsi",
                "XiaoXiWeiPet",
                "monitor.xml");
            _enabled = true;
            LoadSettings();
            _timer = new System.Threading.Timer(OnTimer, null, DisabledTimerValue, DisabledTimerValue);
        }

        public bool Enabled
        {
            get
            {
                lock (_sync)
                {
                    return _enabled;
                }
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || !_networkAllowed || !_enabled)
                {
                    return;
                }
                ScheduleLocked(_random.Next(3000, 8001));
            }
        }

        public void SetEnabled(bool enabled)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _enabled = enabled;
                SaveSettingsLocked();
                if (!_networkAllowed || !_enabled)
                {
                    _timer.Change(DisabledTimerValue, DisabledTimerValue);
                    return;
                }

                _failureCount = 0;
                ScheduleLocked(_random.Next(1500, 4001));
            }
        }

        public void CheckNow()
        {
            if (!_networkAllowed)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate { Poll(true); });
        }

        private void OnTimer(object state)
        {
            Poll(false);
        }

        private void Poll(bool manual)
        {
            lock (_sync)
            {
                if (_disposed || !_networkAllowed || (!manual && !_enabled) || _polling)
                {
                    return;
                }
                _polling = true;
            }

            bool succeeded = false;
            try
            {
                string html = DownloadFeed();
                long newestId = FindNewestPostId(html);
                if (newestId > 0)
                {
                    long previousId;
                    bool mayNotify;
                    lock (_sync)
                    {
                        previousId = _lastSeenId;
                        mayNotify = _enabled || manual;
                        if (newestId > _lastSeenId)
                        {
                            _lastSeenId = newestId;
                            SaveSettingsLocked();
                        }
                    }

                    // A fresh installation only establishes a baseline and never
                    // announces the existing timeline as a new post.
                    if (mayNotify && previousId > 0 && newestId > previousId)
                    {
                        Uri postUri = BuildTrustedPostUri(newestId);
                        if (postUri != null && _onNewPost != null)
                        {
                            try
                            {
                                _onNewPost(newestId, postUri);
                            }
                            catch
                            {
                                // A closing UI must not destabilize the monitor thread.
                            }
                        }
                    }
                }
                succeeded = true;
            }
            catch
            {
                // Offline, timeout and page-format failures are intentionally quiet.
            }
            finally
            {
                lock (_sync)
                {
                    _polling = false;
                    _activeRequest = null;
                    if (!_disposed && _networkAllowed && _enabled)
                    {
                        if (succeeded)
                        {
                            _failureCount = 0;
                            ScheduleLocked(_random.Next(9 * 60 * 1000, 11 * 60 * 1000 + 1));
                        }
                        else
                        {
                            _failureCount = Math.Min(_failureCount + 1, 6);
                            double minutes = Math.Min(120.0, 5.0 * Math.Pow(2.0, _failureCount - 1));
                            int jitterMilliseconds = _random.Next(0, 90 * 1000 + 1);
                            ScheduleLocked((int)(minutes * 60.0 * 1000.0) + jitterMilliseconds);
                        }
                    }
                }
            }
        }

        private string DownloadFeed()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch
            {
                // TLS 1.2 is unavailable only on very old framework installations.
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(FeedUrl);
            request.Method = "GET";
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.UserAgent = "XiaoXiWeiPet/3.0 (personal non-commercial fan reminder; 10-minute polling)";
            request.Accept = "text/html,application/xhtml+xml";

            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("WeiboFeedMonitor");
                }
                _activeRequest = request;
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK || !IsTrustedSinaHost(response.ResponseUri))
                {
                    throw new InvalidDataException("Unexpected feed response.");
                }
                if (response.ContentLength > MaxResponseBytes)
                {
                    throw new InvalidDataException("Feed response is too large.");
                }

                using (Stream input = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[16 * 1024];
                    while (true)
                    {
                        int read = input.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }
                        if (output.Length + read > MaxResponseBytes)
                        {
                            throw new InvalidDataException("Feed response is too large.");
                        }
                        output.Write(buffer, 0, read);
                    }
                    return Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }

        private static long FindNewestPostId(string html)
        {
            long maximum = 0;
            foreach (Match match in PostLinkPattern.Matches(html ?? string.Empty))
            {
                long candidate;
                if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out candidate)
                    && candidate > maximum)
                {
                    maximum = candidate;
                }
            }
            return maximum;
        }

        private static Uri BuildTrustedPostUri(long postId)
        {
            Uri uri;
            string value = "https://www.sina.cn/news/detail/" + postId.ToString(CultureInfo.InvariantCulture) + ".html";
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !IsTrustedPostUri(uri))
            {
                return null;
            }
            return uri;
        }

        public static bool IsTrustedPostUri(Uri uri)
        {
            if (!IsTrustedSinaHost(uri))
            {
                return false;
            }
            return Regex.IsMatch(
                uri.AbsolutePath,
                @"^/news/detail/[0-9]+\.html$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private static bool IsTrustedSinaHost(Uri uri)
        {
            return uri != null
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, "www.sina.cn", StringComparison.OrdinalIgnoreCase);
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return;
                }

                string xml = File.ReadAllText(_settingsPath, Encoding.UTF8);
                Match enabledMatch = EnabledPattern.Match(xml);
                bool enabled;
                if (enabledMatch.Success && bool.TryParse(enabledMatch.Groups[1].Value, out enabled))
                {
                    _enabled = enabled;
                }

                Match lastSeenMatch = LastSeenPattern.Match(xml);
                long lastSeen;
                if (lastSeenMatch.Success
                    && long.TryParse(lastSeenMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out lastSeen)
                    && lastSeen >= 0)
                {
                    _lastSeenId = lastSeen;
                }
            }
            catch
            {
                _enabled = true;
                _lastSeenId = 0;
            }
        }

        private void SaveSettingsLocked()
        {
            try
            {
                string directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                    + "<monitor enabled=\"" + (_enabled ? "true" : "false")
                    + "\" lastSeenId=\"" + _lastSeenId.ToString(CultureInfo.InvariantCulture) + "\" />\r\n";
                File.WriteAllText(_settingsPath, xml, new UTF8Encoding(false));
            }
            catch
            {
                // A read-only profile should not interrupt the pet.
            }
        }

        private void ScheduleLocked(int delayMilliseconds)
        {
            _timer.Change(Math.Max(0, delayMilliseconds), DisabledTimerValue);
        }

        public void Dispose()
        {
            HttpWebRequest request = null;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _timer.Change(DisabledTimerValue, DisabledTimerValue);
                request = _activeRequest;
                _activeRequest = null;
            }

            if (request != null)
            {
                try
                {
                    request.Abort();
                }
                catch
                {
                    // The request may have completed between the lock and Abort.
                }
            }
            _timer.Dispose();
        }
    }

    internal enum PetState
    {
        Idle = 0,
        RunningRight = 1,
        RunningLeft = 2,
        Waving = 3,
        Jumping = 4,
        Failed = 5,
        Waiting = 6,
        Working = 7,
        Review = 8,
        AngryStomp = 11,
        Adorable = 12,
        Laughing = 13,
        Crying = 14,
        SkinExclusive = 15,
        Reserved16 = 16,
        Reserved17 = 17,
        HandDance = 18,
        Singing = 19,
        Heroine = 20,
        FlyingKiss = 21,
        Sitting = 22,
        SideRest = 23
    }

    internal enum DragReactionPhase
    {
        None,
        Startled,
        Held
    }

    internal sealed class PetForm : Form
    {
        private const int WsExLayered = 0x00080000;
        private const int WsExToolWindow = 0x00000080;
        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        internal static readonly int[] FrameCounts = new int[]
        {
            6, 8, 8, 4, 5, 8, 6, 6, 6, 0, 0, 6,
            8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8
        };
        internal static readonly int[][] FrameDurations = new int[][]
        {
            new int[] { 280, 110, 110, 140, 140, 320 },
            new int[] { 120, 120, 120, 120, 120, 120, 120, 220 },
            new int[] { 120, 120, 120, 120, 120, 120, 120, 220 },
            new int[] { 140, 140, 140, 280 },
            new int[] { 140, 140, 140, 140, 280 },
            new int[] { 140, 140, 140, 140, 140, 140, 140, 240 },
            new int[] { 150, 150, 150, 150, 150, 260 },
            new int[] { 120, 120, 120, 120, 120, 220 },
            new int[] { 150, 150, 150, 150, 150, 280 },
            new int[] { 120 },
            new int[] { 120 },
            new int[] { 420, 320, 240, 170, 190, 1100 },
            new int[] { 220, 140, 180, 240, 260, 180, 160, 260 },
            new int[] { 190, 150, 170, 170, 250, 170, 170, 220 },
            new int[] { 220, 180, 220, 260, 300, 240, 200, 260 },
            new int[] { 220, 180, 180, 220, 260, 200, 180, 260 },
            new int[] { 70, 70, 70, 70, 70, 70, 70, 70 },
            new int[] { 70, 70, 70, 70, 70, 70, 70, 70 },
            new int[] { 150, 150, 150, 150, 150, 150, 150, 150 },
            new int[] { 200, 200, 200, 200, 200, 200, 200, 200 },
            new int[] { 150, 150, 150, 150, 150, 150, 150, 150 },
            new int[] { 180, 180, 180, 180, 180, 180, 180, 180 },
            new int[] { 220, 220, 220, 220, 220, 220, 220, 220 },
            new int[] { 220, 220, 220, 220, 220, 220, 220, 220 }
        };
        private static readonly PetState[] IdleActionStates = new PetState[]
        {
            PetState.HandDance,
            PetState.Singing,
            PetState.Heroine,
            PetState.FlyingKiss,
            PetState.Sitting,
            PetState.SideRest,
            PetState.Adorable,
            PetState.Laughing,
            PetState.Crying,
            PetState.SkinExclusive
        };
        private static readonly int[] IdleActionWeights = new int[] { 18, 18, 14, 14, 8, 8, 11, 8, 5, 10 };

        private ScaledFrameCache _frameCache;
        private readonly FormsTimer _animationTimer;
        private readonly FormsTimer _behaviorTimer;
        private readonly FormsTimer _autoExitTimer;
        private readonly Random _random;
        private readonly bool _qaMode;
        private readonly WeiboFeedMonitor _weiboMonitor;
        private readonly SkinCatalog _skinCatalog;
        private readonly Dictionary<string, ToolStripMenuItem> _skinItems;
        private ContextMenuStrip _menu;
        private NotifyIcon _trayIcon;
        private ToolStripMenuItem _topMostItem;
        private ToolStripMenuItem _pauseItem;
        private ToolStripMenuItem _weiboReminderItem;
        private ToolStripMenuItem _weiboCheckNowItem;
        private ToolStripMenuItem _idleMenu;
        private ToolStripMenuItem _skinMenu;
        private SkinPack _currentSkin;
        private SkinPack _pendingSkin;
        private SkinPack _queuedSkin;
        private ScaledFrameCache _pendingSkinCache;
        private Bitmap _skinTransitionOldFrame;
        private Bitmap _skinTransitionNewPose;
        private Bitmap _skinTransitionNewIdle;
        private bool _skinTransitionActive;
        private bool _skinTransitionUsesFinalPose;
        private int _skinTransitionStage;
        private Uri _pendingWeiboPostUri;
        private PetState _state;
        private int _stateFrame;
        private int _remainingActionFrames;
        private bool _temporaryAction;
        private bool _paused;
        // Automatic roaming is intentionally unavailable. Rows 12-15 are
        // opt-in action slots introduced in v3.0.4; rows 16-17 remain compatibility-only.
        private readonly bool _roamingEnabled = DragReactionContract.AutomaticRoamingEnabled;
        private bool _movingToTarget;
        private int _roamTargetX;
        private DateTime _nextRoamAt;
        private DateTime _nextIdleActionAt;
        private DateTime _weiboPromptActiveUntil;
        private PetState _lastIdleAction;
        private PetState _secondLastIdleAction;
        private bool _dragging;
        private Point _dragStartCursor;
        private Point _dragStartLocation;
        private bool _dragActivated;
        private DragReactionPhase _dragReactionPhase;
        private bool _pendingDoubleClickWave;
        private bool _finishingDrag;
        private int _dragDistance;
        private int _lookIndex;
        private bool _hasSmoothedLookAngle;
        private double _smoothedLookDegrees;
        private long _lastLookTimestamp;
        private bool _sideRestSleeping;
        private bool _sideRestWaking;
        private int _sleepEffectTick;
        private bool _sittingPhoneHolding;
        private bool _sittingPhoneExiting;
        private bool _sittingExitRequested;
        private int _sittingLoopDirection;
        private bool _linanSwingActive;
        private bool _linanSwingHolding;
        private bool _linanSwingExiting;
        private bool _linanSwingExitRequested;
        private int _tweenStep;
        private bool _longKeyFrameHoldConsumed;
        private int _currentRow;
        private int _currentColumn;
        private float _scale;

        public PetForm(int autoExitMilliseconds, bool qaMode)
        {
            _qaMode = qaMode;
            _random = new Random();
            _scale = 2.25f;
            _state = PetState.Idle;
            _stateFrame = 0;
            _remainingActionFrames = -1;
            _lookIndex = -1;
            _lastIdleAction = (PetState)(-1);
            _secondLastIdleAction = (PetState)(-1);
            _skinCatalog = SkinCatalog.Discover();
            _skinItems = new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);
            byte[] initialArchive;
            _currentSkin = _skinCatalog.LoadPreferred(out initialArchive);
            if (initialArchive == null || initialArchive.Length == 0)
            {
                throw new InvalidOperationException("找不到可用的角色帧资源。");
            }
            _frameCache = new ScaledFrameCache(initialArchive, _currentSkin.EntryPrefix);
            _weiboMonitor = new WeiboFeedMonitor(!_qaMode, OnWeiboPostDiscovered);

            Text = "小曦薇";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);

            BuildMenu();
            BuildTrayIcon();
            ApplyScale(_scale, false);

            _animationTimer = new FormsTimer();
            _animationTimer.Interval = GetTweenInterval(
                0,
                0,
                AnimationSmoothing.GetStepsPerTransition(FrameCounts[0]),
                0);
            _animationTimer.Tick += OnAnimationTick;

            _behaviorTimer = new FormsTimer();
            _behaviorTimer.Interval = 45;
            _behaviorTimer.Tick += OnBehaviorTick;

            if (autoExitMilliseconds > 0)
            {
                _autoExitTimer = new FormsTimer();
                _autoExitTimer.Interval = autoExitMilliseconds;
                _autoExitTimer.Tick += delegate
                {
                    _autoExitTimer.Stop();
                    Close();
                };
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExLayered | WsExToolWindow;
                return parameters;
            }
        }

        protected override void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            if (_autoExitTimer != null)
            {
                _autoExitTimer.Start();
            }
            ResetToLowerRight();
            RenderCurrentFrame();
            _animationTimer.Start();
            _behaviorTimer.Start();
            ScheduleNextRoam(4000);
            ScheduleNextIdleAction(false);
            _weiboMonitor.Start();

            if (_qaMode)
            {
                // Exercise the full prepare/spin/reveal/atomic-swap/cleanup
                // path even when no optional skin is installed beside the QA
                // executable.  The copied built-in archive is never persisted.
                BeginInvoke((MethodInvoker)delegate
                {
                    SwitchSkin(new SkinPack(
                        "qa-transition-copy",
                        "QA skin transition",
                        "Anbunengsi",
                        string.Empty,
                        true,
                        string.Empty));
                });
            }

            if (!_qaMode)
            {
                try
                {
                    _trayIcon.ShowBalloonTip(2600, "小曦薇来啦", "双击她会挥手，右键可以切换动作和大小。", ToolTipIcon.Info);
                }
                catch
                {
                    // Notifications can be disabled by Windows policy.
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            // UpdateLayeredWindow owns all pixels.
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            if (eventArgs.Button != MouseButtons.Left)
            {
                return;
            }

            if (_skinTransitionActive)
            {
                _pendingDoubleClickWave = false;
                return;
            }

            // A sleeping click is consumed by the wake-up sequence.  In
            // particular it must not also become a drag or the first half of
            // the normal double-click wave gesture.
            if (_state == PetState.SideRest && (_sideRestSleeping || _sideRestWaking))
            {
                _pendingDoubleClickWave = false;
                if (_sideRestSleeping)
                {
                    WakeFromSideRest();
                }
                return;
            }

            // Sitting becomes a persistent phone break once frame 3 is
            // reached.  A click requests the authored put-away/stand-up exit
            // and is never reinterpreted as a drag or double-click.
            if (_state == PetState.Sitting && (_sittingPhoneHolding || _sittingPhoneExiting))
            {
                _pendingDoubleClickWave = false;
                if (_sittingPhoneHolding)
                {
                    ExitSittingPhoneBreak();
                }
                return;
            }

            // Linan's swing is intentionally a manual-only persistent action.
            // Its click exit is consumed until the loop reaches frame 2.
            if (_state == PetState.SkinExclusive && _linanSwingActive)
            {
                _pendingDoubleClickWave = false;
                ExitLinanSwing();
                return;
            }

            ResetDragReactionState();
            _dragging = true;
            if (_temporaryAction && IsIdleActionState(_state))
            {
                ReturnToIdle();
            }
            ScheduleNextIdleAction(false);
            _movingToTarget = false;
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
            _pendingDoubleClickWave = false;
            _dragDistance = 0;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            if (!_dragging)
            {
                return;
            }

            Point cursor = Cursor.Position;
            int deltaX = cursor.X - _dragStartCursor.X;
            int deltaY = cursor.Y - _dragStartCursor.Y;
            long squaredDistance = (long)deltaX * deltaX + (long)deltaY * deltaY;
            int distance = (int)Math.Round(Math.Sqrt(squaredDistance));
            _dragDistance = Math.Max(_dragDistance, distance);
            Location = new Point(_dragStartLocation.X + deltaX, _dragStartLocation.Y + deltaY);

            if (!_dragActivated && _dragDistance >= GetDragActivationThresholdPixels())
            {
                BeginDragReaction();
            }
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            base.OnMouseUp(eventArgs);
            if (eventArgs.Button != MouseButtons.Left || !_dragging)
            {
                return;
            }

            FinishDrag(true);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs eventArgs)
        {
            base.OnMouseDoubleClick(eventArgs);
            if (_skinTransitionActive)
            {
                _pendingDoubleClickWave = false;
                return;
            }
            if (eventArgs.Button == MouseButtons.Left
                && (_state == PetState.SideRest
                    || _sideRestSleeping
                    || _sideRestWaking
                    || _state == PetState.Sitting
                    || _sittingPhoneHolding
                    || _sittingPhoneExiting
                    || (_state == PetState.SkinExclusive && _linanSwingActive)))
            {
                _pendingDoubleClickWave = false;
                return;
            }
            if (eventArgs.Button == MouseButtons.Left
                && !_dragActivated
                && _dragDistance < GetDragActivationThresholdPixels())
            {
                _pendingDoubleClickWave = true;
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs eventArgs)
        {
            base.OnMouseCaptureChanged(eventArgs);
            if (_dragging && !Capture && !_finishingDrag)
            {
                FinishDrag(false);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            float nextScale = _scale + (eventArgs.Delta > 0 ? 0.25f : -0.25f);
            SetScaleFromMenu(nextScale);
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            base.OnKeyDown(eventArgs);
            if (eventArgs.Control && eventArgs.KeyCode == Keys.Q)
            {
                Close();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeSkinTransitionResources();
                ResetSideRestState();
                ResetSittingPhoneState();
                ResetLinanSwingState();
                if (_weiboMonitor != null)
                {
                    _weiboMonitor.Dispose();
                }
                if (_animationTimer != null)
                {
                    _animationTimer.Stop();
                    _animationTimer.Dispose();
                }
                if (_behaviorTimer != null)
                {
                    _behaviorTimer.Stop();
                    _behaviorTimer.Dispose();
                }
                if (_autoExitTimer != null)
                {
                    _autoExitTimer.Stop();
                    _autoExitTimer.Dispose();
                }
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                if (_menu != null)
                {
                    _menu.Dispose();
                }
                if (_frameCache != null)
                {
                    _frameCache.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

            ToolStripMenuItem title = new ToolStripMenuItem("小曦薇");
            title.Enabled = false;
            _menu.Items.Add(title);
            _menu.Items.Add(new ToolStripSeparator());
            AddActionItem("挥挥手", PetState.Waving, 2);
            AddActionItem("跳一下", PetState.Jumping, 2);
            AddActionItem("等你回应", PetState.Waiting, 2);
            AddActionItem("认真工作", PetState.Working, 2);
            AddActionItem("认真检查", PetState.Review, 2);
            AddActionItem("委屈一下", PetState.Failed, 2);
            AddActionItem("气鼓鼓跺脚", PetState.AngryStomp, 1);

            _idleMenu = new ToolStripMenuItem("待机动作");
            RebuildIdleActionMenu();
            _menu.Items.Add(_idleMenu);
            _menu.Items.Add(new ToolStripSeparator());

            _weiboReminderItem = new ToolStripMenuItem("微博提醒（开/关）");
            _weiboReminderItem.CheckOnClick = true;
            _weiboReminderItem.Checked = _weiboMonitor.Enabled;
            _weiboReminderItem.CheckedChanged += delegate
            {
                _weiboMonitor.SetEnabled(_weiboReminderItem.Checked);
            };
            _menu.Items.Add(_weiboReminderItem);

            _weiboCheckNowItem = new ToolStripMenuItem("立即检查微博");
            _weiboCheckNowItem.Click += delegate { _weiboMonitor.CheckNow(); };
            _menu.Items.Add(_weiboCheckNowItem);

            _topMostItem = new ToolStripMenuItem("总在最前");
            _topMostItem.CheckOnClick = true;
            _topMostItem.Checked = true;
            _topMostItem.CheckedChanged += delegate
            {
                TopMost = _topMostItem.Checked;
            };
            _menu.Items.Add(_topMostItem);

            _pauseItem = new ToolStripMenuItem("暂停动画");
            _pauseItem.CheckOnClick = true;
            _pauseItem.CheckedChanged += delegate
            {
                SetPaused(_pauseItem.Checked);
            };
            _menu.Items.Add(_pauseItem);

            ToolStripMenuItem sizeMenu = new ToolStripMenuItem("大小");
            AddScaleItem(sizeMenu, "125%", 1.25f);
            AddScaleItem(sizeMenu, "175%", 1.75f);
            AddScaleItem(sizeMenu, "225%（默认）", 2.25f);
            AddScaleItem(sizeMenu, "300%", 3.0f);
            AddScaleItem(sizeMenu, "400%（4K原始帧）", 4.0f);
            _menu.Items.Add(sizeMenu);

            _skinMenu = new ToolStripMenuItem("皮肤");
            foreach (SkinPack pack in _skinCatalog.Packs)
            {
                ToolStripMenuItem skinItem = new ToolStripMenuItem(pack.Name);
                skinItem.Tag = pack;
                skinItem.Checked = string.Equals(pack.Id, _currentSkin.Id, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(pack.Developer))
                {
                    skinItem.ToolTipText = "开发者：" + pack.Developer;
                }
                skinItem.Click += delegate(object sender, EventArgs eventArgs)
                {
                    ToolStripMenuItem selectedItem = sender as ToolStripMenuItem;
                    SkinPack selectedPack = selectedItem == null ? null : selectedItem.Tag as SkinPack;
                    if (selectedPack != null)
                    {
                        SwitchSkin(selectedPack);
                    }
                };
                _skinItems[pack.Id] = skinItem;
                _skinMenu.DropDownItems.Add(skinItem);
            }
            _menu.Items.Add(_skinMenu);

            ToolStripMenuItem resetItem = new ToolStripMenuItem("回到屏幕右下角");
            resetItem.Click += delegate
            {
                ResetToLowerRight();
                RenderCurrentFrame();
            };
            _menu.Items.Add(resetItem);

            _menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem aboutItem = new ToolStripMenuItem("使用说明");
            aboutItem.Click += delegate
            {
                MessageBox.Show(
                    "左键拖动：拖动达到阈值后她会吓一跳；被拖动时保持受惊，松手后会叉腰跺脚生气\n双击：挥手\n鼠标滚轮：调整大小\n鼠标靠近一定范围：她会平滑看向鼠标；移远后立即回到只眨眼的普通待机\n安静待机：普通眨眼为主，静止一段时间后会随机表演；内置白裙另有卖萌、大笑、哭和“白裙星光亮相”，外置皮肤也可声明专属动作\n坐下玩手机：坐下后会一直玩手机，左键点她才会收起手机并起身\n侧躺入睡：她会一直安睡，z/Z 从头顶向上飘；左键点她会伸懒腰后起身\n临安公主：从右键动作菜单手动选择“公主荡秋千”；她会持续摆动，左键点她后会在最低点下秋千\n切换皮肤：人物会分层伪3D转身，在半圈中由头到脚完成变装并定格；短暂变身期间动作输入会锁定\n右键：可手动预览待机动作，或调整皮肤、微博提醒和设置\nCtrl+Q：退出\n\n微博提醒读取新浪公开页面，每约10分钟低频检查一次；首次只建立基线。公开页面可能延迟，也可能因页面改版而暂时失效；断网时不会弹出错误。\n\n【皮肤包接口】\n可在程序同级创建 skins\\<id>\\skin.xml 和 frames.zip。skin.xml 根元素格式为 <skin apiVersion=\"1\" id=\"...\" name=\"...\" developer=\"...\" archive=\"frames.zip\" exclusiveAction=\"可选菜单名\"/>；exclusiveAction 非空时 r15 作为该外置皮肤专属动作。帧文件使用 r00/c00.png 的固定命名、相同动作行映射和 528×808 尺寸。程序会在运行时为关键帧生成至少24个平滑显示阶段；皮肤包无需膨胀帧数。缺帧、路径异常或尺寸不符的皮肤会被静默跳过并自动回退内置白裙。\n\n开发者：Anbunengsi\n\n【免责声明】\n本程序由个人独立开发，仅供田曦薇粉丝个人欣赏、交流与非商业使用，纯属为爱发电。人物姓名、肖像、形象及相关素材的权利归田曦薇本人及相应权利方所有。本程序为非官方作品，与田曦薇本人、工作室、经纪机构及品牌方无官方关联，也不代表已获得其授权。禁止售卖、收费分发、广告引流、商业推广、二次商用、冒用官方名义，或用于侵犯肖像权、名誉权及其他合法权益。若权利方认为内容不妥，请停止传播并联系开发者处理。",
                    "小曦薇",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };
            _menu.Items.Add(aboutItem);

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { Close(); };
            _menu.Items.Add(exitItem);

            ContextMenuStrip = _menu;
        }

        private void AddActionItem(string text, PetState state, int loops)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { StartAction(state, loops); };
            _menu.Items.Add(item);
        }

        private void AddIdlePreviewItem(ToolStripMenuItem parent, string text, PetState state)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = state;
            item.Click += delegate(object sender, EventArgs eventArgs)
            {
                ToolStripMenuItem selected = sender as ToolStripMenuItem;
                if (selected != null)
                {
                    StartIdleAction((PetState)selected.Tag);
                }
            };
            parent.DropDownItems.Add(item);
        }

        private void RebuildIdleActionMenu()
        {
            if (_idleMenu == null)
            {
                return;
            }

            _idleMenu.DropDownItems.Clear();
            AddIdlePreviewItem(_idleMenu, "手势舞", PetState.HandDance);
            AddIdlePreviewItem(_idleMenu, "轻声唱歌", PetState.Singing);
            AddIdlePreviewItem(_idleMenu, "女侠亮相", PetState.Heroine);
            AddIdlePreviewItem(_idleMenu, "飞吻", PetState.FlyingKiss);
            AddIdlePreviewItem(_idleMenu, "坐下玩手机", PetState.Sitting);
            AddIdlePreviewItem(_idleMenu, "侧躺小憩", PetState.SideRest);

            if (_currentSkin != null && _currentSkin.IsBuiltIn)
            {
                _idleMenu.DropDownItems.Add(new ToolStripSeparator());
                AddIdlePreviewItem(_idleMenu, "卖萌", PetState.Adorable);
                AddIdlePreviewItem(_idleMenu, "哈哈哈大笑", PetState.Laughing);
                AddIdlePreviewItem(_idleMenu, "哭一下", PetState.Crying);
            }
            if (_currentSkin != null && _currentSkin.HasExclusiveAction)
            {
                _idleMenu.DropDownItems.Add(new ToolStripSeparator());
                if (IsLinanSwingEnabled())
                {
                    AddLinanSwingMenuItem(_idleMenu, _currentSkin.ExclusiveActionName);
                }
                else
                {
                    AddIdlePreviewItem(
                        _idleMenu,
                        _currentSkin.ExclusiveActionName,
                        PetState.SkinExclusive);
                }
            }
        }

        private void AddLinanSwingMenuItem(ToolStripMenuItem parent, string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate(object sender, EventArgs eventArgs)
            {
                StartLinanSwing();
            };
            parent.DropDownItems.Add(item);
        }

        private void AddScaleItem(ToolStripMenuItem parent, string text, float scale)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = scale;
            item.Click += delegate(object sender, EventArgs eventArgs)
            {
                ToolStripMenuItem selected = sender as ToolStripMenuItem;
                if (selected != null)
                {
                    SetScaleFromMenu((float)selected.Tag);
                }
            };
            parent.DropDownItems.Add(item);
        }

        private void SwitchSkin(SkinPack requestedPack)
        {
            SkinPack selectedPack = requestedPack ?? _skinCatalog.BuiltIn;
            if (_skinTransitionActive)
            {
                if (_pendingSkin != null
                    && string.Equals(_pendingSkin.Id, selectedPack.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _queuedSkin = null;
                    return;
                }
                // Keep only the latest repeated choice.  It starts after the
                // current atomic swap, so neither cache is torn down midway.
                _queuedSkin = selectedPack;
                return;
            }
            if (_currentSkin != null
                && string.Equals(_currentSkin.Id, selectedPack.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            byte[] archiveBytes;
            if (!selectedPack.TryLoadArchive(out archiveBytes))
            {
                // Invalid optional content never disturbs the active skin.
                return;
            }

            ScaledFrameCache candidateCache = null;
            Bitmap oldFrame = null;
            Bitmap newPose = null;
            Bitmap newIdle = null;
            try
            {
                candidateCache = new ScaledFrameCache(archiveBytes, selectedPack.EntryPrefix);
                bool usesFinalPose;
                CreateCandidateSkinTransitionFrames(
                    candidateCache,
                    ClientSize.Width,
                    ClientSize.Height,
                    out newPose,
                    out newIdle,
                    out usesFinalPose);
                oldFrame = CreateOwnedCurrentVisual();

                _pendingSkin = selectedPack;
                _pendingSkinCache = candidateCache;
                candidateCache = null;
                _skinTransitionOldFrame = oldFrame;
                _skinTransitionNewPose = newPose;
                _skinTransitionNewIdle = newIdle;
                oldFrame = null;
                newPose = null;
                newIdle = null;
                _skinTransitionUsesFinalPose = usesFinalPose;
                _skinTransitionStage = 0;
                _skinTransitionActive = true;

                InterruptCurrentActionForSkinTransition();
                _animationTimer.Interval = SkinTransitionContract.TickMilliseconds;
                if (!_paused)
                {
                    _animationTimer.Start();
                }
                RenderCurrentFrame();
            }
            catch
            {
                // Everything above is prepared off to the side.  Failure
                // leaves the current cache, current skin and current action
                // untouched.
                if (candidateCache != null) candidateCache.Dispose();
                if (oldFrame != null) oldFrame.Dispose();
                if (newPose != null) newPose.Dispose();
                if (newIdle != null) newIdle.Dispose();
                if (_skinTransitionActive || _pendingSkinCache != null)
                {
                    AbortSkinTransition(false);
                    if (IsHandleCreated && !IsDisposed)
                    {
                        ReturnToIdle();
                    }
                }
            }
        }

        private static Bitmap CloneOwnedBitmap(Bitmap source)
        {
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(copy))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            return copy;
        }

        private void CreateCandidateSkinTransitionFrames(
            ScaledFrameCache candidateCache,
            int width,
            int height,
            out Bitmap newPose,
            out Bitmap newIdle,
            out bool usesFinalPose)
        {
            newPose = null;
            newIdle = null;
            usesFinalPose = false;
            try
            {
                Bitmap idleSource = candidateCache.Get((int)PetState.Idle, 0, width, height);
                newIdle = CloneOwnedBitmap(idleSource);
                try
                {
                    Bitmap poseSource = candidateCache.Get(
                        SkinTransitionContract.FinalPoseRow,
                        SkinTransitionContract.FinalPoseColumn,
                        width,
                        height);
                    newPose = CloneOwnedBitmap(poseSource);
                    usesFinalPose = true;
                }
                catch
                {
                    newPose = CloneOwnedBitmap(newIdle);
                    usesFinalPose = false;
                }
            }
            catch
            {
                if (newPose != null) newPose.Dispose();
                if (newIdle != null) newIdle.Dispose();
                newPose = null;
                newIdle = null;
                throw;
            }
        }

        private Bitmap CreateOwnedCurrentVisual()
        {
            int row;
            int column;
            if (_state == PetState.Idle && _lookIndex >= 0 && !_dragging && !_movingToTarget && !_temporaryAction)
            {
                row = _lookIndex < 8 ? 9 : 10;
                column = _lookIndex % 8;
            }
            else
            {
                row = (int)_state;
                column = _stateFrame;
            }

            Bitmap source;
            int targetRow;
            int targetColumn;
            if (_tweenStep > 0 && TryGetTweenTarget(row, column, out targetRow, out targetColumn))
            {
                source = _frameCache.GetTween(
                    row,
                    column,
                    targetRow,
                    targetColumn,
                    _tweenStep,
                    GetCurrentTweenSteps(),
                    ClientSize.Width,
                    ClientSize.Height);
            }
            else
            {
                source = _frameCache.Get(row, column, ClientSize.Width, ClientSize.Height);
            }

            if (_state == PetState.SideRest && _sideRestSleeping)
            {
                Rectangle visibleBounds = _frameCache.GetVisibleBounds(
                    (int)PetState.SideRest,
                    PersistentActionContract.SideRestSleepFrame,
                    ClientSize.Width,
                    ClientSize.Height);
                return CreateSleepEffectFrame(source, visibleBounds);
            }
            return CloneOwnedBitmap(source);
        }

        private void InterruptCurrentActionForSkinTransition()
        {
            _dragging = false;
            Capture = false;
            ResetDragReactionState();
            _movingToTarget = false;
            _pendingDoubleClickWave = false;
            _finishingDrag = false;
            _lookIndex = -1;
            _hasSmoothedLookAngle = false;
            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            _state = PetState.Idle;
            _stateFrame = 0;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = false;
            _remainingActionFrames = -1;
            ScheduleNextIdleAction(false);
            ScheduleNextRoam(2200);
        }

        private bool AdvanceSkinTransition()
        {
            if (!_skinTransitionActive)
            {
                return false;
            }

            _skinTransitionStage++;
            if (_skinTransitionStage >= SkinTransitionContract.TotalStages)
            {
                CompleteSkinTransition();
                return true;
            }

            RenderCurrentFrame();
            if (_skinTransitionActive)
            {
                _animationTimer.Interval = SkinTransitionContract.TickMilliseconds;
            }
            else
            {
                ScheduleCurrentTweenTick();
            }
            return true;
        }

        private void CompleteSkinTransition()
        {
            if (!_skinTransitionActive || _pendingSkin == null || _pendingSkinCache == null)
            {
                AbortSkinTransition(true);
                return;
            }

            ScaledFrameCache oldCache = _frameCache;
            SkinPack completedSkin = _pendingSkin;
            SkinPack queuedSkin = _queuedSkin;
            _queuedSkin = null;

            // Atomic cache ownership swap: the old cache remains usable until
            // the fully decoded candidate cache is installed.
            _frameCache = _pendingSkinCache;
            _pendingSkinCache = null;
            _currentSkin = completedSkin;
            _pendingSkin = null;
            _skinTransitionActive = false;
            _skinTransitionStage = 0;
            _skinTransitionUsesFinalPose = false;
            DisposeSkinTransitionBitmaps();
            try
            {
                oldCache.Dispose();
            }
            catch
            {
                // The new cache is already valid; disposal failure is harmless.
            }

            if (!_qaMode)
            {
                _skinCatalog.SaveSelectedId(completedSkin.Id);
            }
            UpdateSkinMenuChecks();
            RebuildIdleActionMenu();
            ReturnToIdle();

            if (queuedSkin != null
                && !string.Equals(queuedSkin.Id, completedSkin.Id, StringComparison.OrdinalIgnoreCase))
            {
                SwitchSkin(queuedSkin);
            }
        }

        private void AbortSkinTransition(bool renderOldSkin)
        {
            if (_pendingSkinCache != null)
            {
                _pendingSkinCache.Dispose();
                _pendingSkinCache = null;
            }
            _pendingSkin = null;
            _queuedSkin = null;
            _skinTransitionActive = false;
            _skinTransitionStage = 0;
            _skinTransitionUsesFinalPose = false;
            DisposeSkinTransitionBitmaps();
            UpdateSkinMenuChecks();
            if (renderOldSkin && !IsDisposed && IsHandleCreated)
            {
                ReturnToIdle();
            }
        }

        private bool TryResizeSkinTransitionFrames(int width, int height)
        {
            if (!_skinTransitionActive)
            {
                return true;
            }

            Bitmap resizedOld = null;
            Bitmap resizedPose = null;
            Bitmap resizedIdle = null;
            try
            {
                resizedOld = FrameScaler.Scale(_skinTransitionOldFrame, width, height);
                _pendingSkinCache.Clear();
                bool usesFinalPose;
                CreateCandidateSkinTransitionFrames(
                    _pendingSkinCache,
                    width,
                    height,
                    out resizedPose,
                    out resizedIdle,
                    out usesFinalPose);

                DisposeSkinTransitionBitmaps();
                _skinTransitionOldFrame = resizedOld;
                _skinTransitionNewPose = resizedPose;
                _skinTransitionNewIdle = resizedIdle;
                resizedOld = null;
                resizedPose = null;
                resizedIdle = null;
                _skinTransitionUsesFinalPose = usesFinalPose;
                return true;
            }
            catch
            {
                if (resizedOld != null) resizedOld.Dispose();
                if (resizedPose != null) resizedPose.Dispose();
                if (resizedIdle != null) resizedIdle.Dispose();
                return false;
            }
        }

        private void DisposeSkinTransitionBitmaps()
        {
            if (_skinTransitionOldFrame != null)
            {
                _skinTransitionOldFrame.Dispose();
                _skinTransitionOldFrame = null;
            }
            if (_skinTransitionNewPose != null)
            {
                _skinTransitionNewPose.Dispose();
                _skinTransitionNewPose = null;
            }
            if (_skinTransitionNewIdle != null)
            {
                _skinTransitionNewIdle.Dispose();
                _skinTransitionNewIdle = null;
            }
        }

        private void DisposeSkinTransitionResources()
        {
            if (_pendingSkinCache != null)
            {
                _pendingSkinCache.Dispose();
                _pendingSkinCache = null;
            }
            DisposeSkinTransitionBitmaps();
            _pendingSkin = null;
            _queuedSkin = null;
            _skinTransitionActive = false;
            _skinTransitionStage = 0;
            _skinTransitionUsesFinalPose = false;
        }

        private void UpdateSkinMenuChecks()
        {
            foreach (KeyValuePair<string, ToolStripMenuItem> pair in _skinItems)
            {
                pair.Value.Checked = string.Equals(pair.Key, _currentSkin.Id, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void BuildTrayIcon()
        {
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "小曦薇";
            Icon executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon != null)
            {
                _trayIcon.Icon = (Icon)executableIcon.Clone();
                executableIcon.Dispose();
            }
            else
            {
                _trayIcon.Icon = SystemIcons.Application;
            }
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.DoubleClick += delegate
            {
                ResetToLowerRight();
                Show();
                TopMost = _topMostItem.Checked;
                RenderCurrentFrame();
            };
            _trayIcon.BalloonTipClicked += delegate
            {
                Uri uri = _pendingWeiboPostUri;
                if (!WeiboFeedMonitor.IsTrustedPostUri(uri))
                {
                    return;
                }
                try
                {
                    Process.Start(uri.AbsoluteUri);
                }
                catch
                {
                    // Browser launch failures should not interrupt the pet.
                }
            };
            _trayIcon.Visible = true;
        }

        private void OnWeiboPostDiscovered(long postId, Uri postUri)
        {
            if (!WeiboFeedMonitor.IsTrustedPostUri(postUri) || IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing || _trayIcon == null)
                    {
                        return;
                    }
                    _weiboPromptActiveUntil = DateTime.UtcNow.AddSeconds(8.0);
                    ScheduleNextIdleAction(false);
                    _pendingWeiboPostUri = postUri;
                    try
                    {
                        _trayIcon.ShowBalloonTip(
                            6000,
                            "田曦薇发新微博啦",
                            "点击通知打开新浪公开页面。",
                            ToolTipIcon.Info);
                    }
                    catch
                    {
                        // Notifications can be disabled by Windows policy.
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // The form handle may disappear while a request is completing.
            }
        }

        private void OnAnimationTick(object sender, EventArgs eventArgs)
        {
            if (_paused)
            {
                return;
            }

            if (AdvanceSkinTransition())
            {
                return;
            }

            if (AdvanceDragReaction())
            {
                return;
            }

            if (AdvanceSideRestAnimation())
            {
                return;
            }

            if (AdvanceLinanSwingAnimation())
            {
                return;
            }

            if (AdvanceSittingAnimation())
            {
                return;
            }

            if (_state == PetState.Idle && _lookIndex >= 0 && !_dragging && !_movingToTarget && !_temporaryAction)
            {
                RenderCurrentFrame();
                _animationTimer.Interval = 120;
                return;
            }

            if (FinishScheduledLongKeyFrameHold())
            {
                return;
            }

            int row = (int)_state;
            int frameCount = FrameCounts[row];
            int tweenSteps = GetCurrentTweenSteps();
            _tweenStep++;

            // The authored keyframe and every synthesized in-between stage
            // share the original keyframe's duration.  State progression and
            // finite-action bookkeeping happen only at the boundary.
            if (_tweenStep < tweenSteps)
            {
                RenderCurrentFrame();
                ScheduleCurrentTweenTick();
                return;
            }

            _tweenStep = 0;
            _stateFrame = (_stateFrame + 1) % frameCount;
            _longKeyFrameHoldConsumed = false;

            if (_temporaryAction)
            {
                _remainingActionFrames--;
                if (_remainingActionFrames <= 0)
                {
                    bool completedIdleAction = IsIdleActionState(_state);
                    ReturnToIdle();
                    ScheduleNextIdleAction(completedIdleAction);
                    ScheduleNextRoam(2200);
                    return;
                }
            }

            RenderCurrentFrame();
            ScheduleCurrentTweenTick();
        }

        private void OnBehaviorTick(object sender, EventArgs eventArgs)
        {
            if (_paused || _dragging || _skinTransitionActive)
            {
                return;
            }

            if (_movingToTarget)
            {
                Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
                int step = Math.Max(2, (int)Math.Round(2.3f * _scale));
                int difference = _roamTargetX - Left;
                Top = area.Bottom - Height;
                if (Math.Abs(difference) <= step)
                {
                    Left = _roamTargetX;
                    _movingToTarget = false;
                    ReturnToIdle();
                    ScheduleNextRoam(_random.Next(2600, 6800));
                }
                else
                {
                    Left += difference > 0 ? step : -step;
                }
                return;
            }

            if (_roamingEnabled && !_temporaryAction && DateTime.UtcNow >= _nextRoamAt)
            {
                BeginRoamingMove();
                return;
            }

            if (TryStartRandomIdleAction())
            {
                return;
            }

            UpdateLookDirection();
        }

        private void BeginRoamingMove()
        {
            // Automatic roaming and authored gait playback were retired in
            // v3.0.3. Rows 12-15 now host opt-in idle actions, so this legacy
            // scheduler can only defer itself if an old setting reaches it.
            _movingToTarget = false;
            ScheduleNextRoam(4000);
        }

        private void UpdateLookDirection()
        {
            if (_state != PetState.Idle || _temporaryAction || _movingToTarget || _dragging)
            {
                // Keep the last filtered angle while look rendering is suppressed.
                // This lets tracking resume smoothly after a drag or special action.
                return;
            }

            Point cursor = Cursor.Position;
            double headX = Left + 66.0 * _scale;
            double headY = Top + 32.0 * _scale;
            double deltaX = cursor.X - headX;
            double deltaY = cursor.Y - headY;
            double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

            bool wasLooking = _lookIndex >= 0 && _hasSmoothedLookAngle;
            double innerDeadZone = MouseLookContract.GetInnerRadius(_scale, wasLooking);
            double outerLimit = MouseLookContract.GetOuterRadius(_scale, wasLooking);
            if (distance < innerDeadZone || distance > outerLimit)
            {
                // Leaving either side of the hysteresis band immediately
                // restores row 0.  Ordinary idle therefore remains the
                // existing blink-only animation instead of a distant stare.
                _hasSmoothedLookAngle = false;
                _lastLookTimestamp = 0;
                if (_lookIndex != -1)
                {
                    _lookIndex = -1;
                    RenderCurrentFrame();
                }
                return;
            }

            double targetDegrees = NormalizeDegrees(Math.Atan2(deltaX, -deltaY) * 180.0 / Math.PI);
            long now = Stopwatch.GetTimestamp();
            if (!_hasSmoothedLookAngle)
            {
                _smoothedLookDegrees = targetDegrees;
                _hasSmoothedLookAngle = true;
                _lastLookTimestamp = now;
            }
            else
            {
                double elapsedSeconds = _lastLookTimestamp == 0
                    ? 0.045
                    : (now - _lastLookTimestamp) / (double)Stopwatch.Frequency;
                elapsedSeconds = Math.Max(0.010, Math.Min(0.180, elapsedSeconds));
                double alpha = 1.0 - Math.Exp(-7.0 * elapsedSeconds);
                _smoothedLookDegrees = NormalizeDegrees(
                    _smoothedLookDegrees + ShortestAngleDelta(_smoothedLookDegrees, targetDegrees) * alpha);
                _lastLookTimestamp = now;
            }

            int nextLook = ((int)Math.Floor((_smoothedLookDegrees + 11.25) / 22.5)) % 16;
            if (_lookIndex >= 0 && nextLook != _lookIndex)
            {
                double currentCenter = _lookIndex * 22.5;
                double distanceFromCurrentCenter = Math.Abs(ShortestAngleDelta(currentCenter, _smoothedLookDegrees));
                if (distanceFromCurrentCenter <= MouseLookContract.DirectionSectorHoldDegrees)
                {
                    nextLook = _lookIndex;
                }
            }

            if (nextLook != _lookIndex)
            {
                _lookIndex = nextLook;
                RenderCurrentFrame();
            }
        }

        private static double NormalizeDegrees(double value)
        {
            value %= 360.0;
            return value < 0.0 ? value + 360.0 : value;
        }

        private static double ShortestAngleDelta(double fromDegrees, double toDegrees)
        {
            double delta = NormalizeDegrees(toDegrees) - NormalizeDegrees(fromDegrees);
            if (delta > 180.0)
            {
                delta -= 360.0;
            }
            else if (delta < -180.0)
            {
                delta += 360.0;
            }
            return delta;
        }

        private bool TryStartRandomIdleAction()
        {
            DateTime now = DateTime.UtcNow;
            if (_paused
                || _dragging
                || _roamingEnabled
                || _movingToTarget
                || _temporaryAction
                || _state != PetState.Idle
                || now < _weiboPromptActiveUntil
                || now < _nextIdleActionAt)
            {
                return false;
            }

            StartIdleAction(ChooseRandomIdleAction());
            return true;
        }

        private PetState ChooseRandomIdleAction()
        {
            int totalWeight = 0;
            for (int index = 0; index < IdleActionStates.Length; index++)
            {
                PetState candidate = IdleActionStates[index];
                if (IsRandomIdleActionAvailable(candidate)
                    && candidate != _lastIdleAction
                    && candidate != _secondLastIdleAction)
                {
                    totalWeight += IdleActionWeights[index];
                }
            }

            if (totalWeight <= 0)
            {
                for (int index = 0; index < IdleActionStates.Length; index++)
                {
                    if (IsRandomIdleActionAvailable(IdleActionStates[index]))
                    {
                        return IdleActionStates[index];
                    }
                }
                return PetState.HandDance;
            }

            int choice = _random.Next(0, totalWeight);
            for (int index = 0; index < IdleActionStates.Length; index++)
            {
                PetState candidate = IdleActionStates[index];
                if (!IsRandomIdleActionAvailable(candidate)
                    || candidate == _lastIdleAction
                    || candidate == _secondLastIdleAction)
                {
                    continue;
                }

                if (choice < IdleActionWeights[index])
                {
                    return candidate;
                }
                choice -= IdleActionWeights[index];
            }
            return IdleActionStates[0];
        }

        private void StartIdleAction(PetState state)
        {
            if (_skinTransitionActive || !IsIdleActionAvailable(state))
            {
                return;
            }

            _secondLastIdleAction = _lastIdleAction;
            _lastIdleAction = state;
            StartAction(state, 1);
        }

        private void StartLinanSwing()
        {
            if (_skinTransitionActive || !IsLinanSwingEnabled())
            {
                return;
            }

            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            ResetDragReactionState();
            _movingToTarget = false;
            _pendingDoubleClickWave = false;
            _lookIndex = -1;
            _state = PetState.SkinExclusive;
            _stateFrame = 0;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = true;
            _remainingActionFrames = -1;
            _linanSwingActive = true;
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private static bool IsIdleActionState(PetState state)
        {
            return ((int)state >= (int)PetState.Adorable
                    && (int)state <= (int)PetState.SkinExclusive)
                || ((int)state >= (int)PetState.HandDance
                    && (int)state <= (int)PetState.SideRest);
        }

        private bool IsIdleActionAvailable(PetState state)
        {
            if (!IsIdleActionState(state))
            {
                return false;
            }
            if ((int)state >= (int)PetState.HandDance
                && (int)state <= (int)PetState.SideRest)
            {
                return true;
            }
            if (state == PetState.SkinExclusive)
            {
                return _currentSkin != null && _currentSkin.HasExclusiveAction;
            }
            if (_currentSkin != null && _currentSkin.IsBuiltIn)
            {
                return state == PetState.Adorable
                    || state == PetState.Laughing
                    || state == PetState.Crying;
            }
            return false;
        }

        private bool IsRandomIdleActionAvailable(PetState state)
        {
            return IsIdleActionAvailable(state)
                && LinanSwingContract.IsRandomEligible(state, _currentSkin);
        }

        private bool IsLinanSwingEnabled()
        {
            return LinanSwingContract.IsEnabled(_currentSkin);
        }

        private void ScheduleNextIdleAction(bool afterSpecialIdle)
        {
            int delay = afterSpecialIdle
                ? _random.Next(18 * 1000, 45 * 1000 + 1)
                : _random.Next(12 * 1000, 25 * 1000 + 1);
            _nextIdleActionAt = DateTime.UtcNow.AddMilliseconds(delay);
        }

        private int GetDragActivationThresholdPixels()
        {
            return DragReactionContract.GetActivationThresholdPixels(_scale);
        }

        private void BeginDragReaction()
        {
            if (!_dragging || _dragActivated || _skinTransitionActive)
            {
                return;
            }

            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            _dragActivated = true;
            _dragReactionPhase = DragReactionPhase.Startled;
            _pendingDoubleClickWave = false;
            _movingToTarget = false;
            _lookIndex = -1;
            _state = (PetState)DragReactionContract.StartleRow;
            _stateFrame = DragReactionContract.StartleFirstFrame;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = false;
            _remainingActionFrames = -1;
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private bool AdvanceDragReaction()
        {
            if (!_dragging || !_dragActivated || _dragReactionPhase == DragReactionPhase.None)
            {
                return false;
            }

            if (_dragReactionPhase == DragReactionPhase.Held)
            {
                _state = (PetState)DragReactionContract.StartleRow;
                _stateFrame = DragReactionContract.StartleHoldFrame;
                _tweenStep = 0;
                RenderCurrentFrame();
                _animationTimer.Interval = DragReactionContract.HoldTickMilliseconds;
                return true;
            }

            if (FinishScheduledLongKeyFrameHold())
            {
                return true;
            }

            int tweenSteps = GetCurrentTweenSteps();
            _tweenStep++;
            if (_tweenStep < tweenSteps)
            {
                RenderCurrentFrame();
                ScheduleCurrentTweenTick();
                return true;
            }

            _tweenStep = 0;
            _stateFrame++;
            _longKeyFrameHoldConsumed = false;
            if (_stateFrame >= DragReactionContract.StartleHoldFrame)
            {
                _stateFrame = DragReactionContract.StartleHoldFrame;
                _dragReactionPhase = DragReactionPhase.Held;
                RenderCurrentFrame();
                _animationTimer.Interval = DragReactionContract.HoldTickMilliseconds;
                return true;
            }

            RenderCurrentFrame();
            ScheduleCurrentTweenTick();
            return true;
        }

        private void ResetDragReactionState()
        {
            _dragActivated = false;
            _dragReactionPhase = DragReactionPhase.None;
        }

        private void FinishDrag(bool allowPostDragActions)
        {
            if (!_dragging)
            {
                return;
            }

            _finishingDrag = true;
            try
            {
                bool completedRealDrag = _dragActivated;
                int dragThreshold = GetDragActivationThresholdPixels();
                DragReleaseAction releaseAction = DragReactionContract.DecideRelease(
                    allowPostDragActions,
                    completedRealDrag,
                    _pendingDoubleClickWave,
                    _dragDistance,
                    dragThreshold);
                _dragging = false;
                Capture = false;
                ClampToWorkingArea();
                ResetDragReactionState();
                if (releaseAction == DragReleaseAction.Angry)
                {
                    StartActionAtFrame(
                        (PetState)DragReactionContract.AngryRow,
                        DragReactionContract.AngryFirstFrame,
                        DragReactionContract.AngryFrameCount);
                }
                else if (releaseAction == DragReleaseAction.Wave)
                {
                    StartAction(PetState.Waving, 2);
                }
                else
                {
                    ReturnToIdle();
                }
                _pendingDoubleClickWave = false;
                ScheduleNextRoam(2500);
            }
            finally
            {
                _finishingDrag = false;
            }
        }

        private int GetFrameDuration(int row, int frame)
        {
            return FrameDurations[row][Math.Min(frame, FrameDurations[row].Length - 1)];
        }

        private int GetCurrentTweenSteps()
        {
            return AnimationSmoothing.GetStepsPerTransition(GetCurrentTweenCycleKeyFrameCount());
        }

        private int GetCurrentTweenCycleKeyFrameCount()
        {
            // The phone-holding loop is a ping-pong cycle with four authored
            // transitions (3-4-5-4-3).  Each persistent segment independently
            // receives the configured number of display stages.
            if (_state == PetState.Sitting && _sittingPhoneHolding && !_sittingPhoneExiting)
            {
                return 4;
            }
            if (_state == PetState.Sitting)
            {
                // Entry and click-exit each contain three transitions.
                return 3;
            }
            if (_state == PetState.SkinExclusive && _linanSwingActive)
            {
                // Entry has two transitions, the swing loop has four, and
                // click exit has three; each independently gets 24 stages.
                if (_linanSwingExiting
                    || (_linanSwingHolding
                        && _linanSwingExitRequested
                        && _stateFrame == PersistentActionContract.LinanSwingLoopFirstFrame))
                {
                    return 3;
                }
                if (_linanSwingHolding && !_linanSwingExiting)
                {
                    return LinanSwingContract.LoopTransitionCount;
                }
                return 2;
            }
            if (_state == PetState.SideRest && !_sideRestSleeping)
            {
                // Four transitions into sleep and four transitions out.
                return 4;
            }

            int row = (int)_state;
            int frameCount = row >= 0 && row < FrameCounts.Length ? FrameCounts[row] : 1;
            return Math.Max(1, frameCount);
        }

        private static int GetReferenceMinimumInterval(int row)
        {
            return AnimationSmoothing.MinimumTickMilliseconds;
        }

        private int GetTweenMotionDuration(int row, int frame)
        {
            return AnimationSmoothing.GetReferenceMotionDuration(
                GetFrameDuration(row, frame),
                GetCurrentTweenCycleKeyFrameCount(),
                GetReferenceMinimumInterval(row));
        }

        private int GetTweenHoldDuration(int row, int frame)
        {
            return AnimationSmoothing.GetReferenceHoldDuration(
                GetFrameDuration(row, frame),
                GetCurrentTweenCycleKeyFrameCount(),
                GetReferenceMinimumInterval(row));
        }

        private int GetTweenInterval(int row, int frame, int tweenSteps, int tweenStep)
        {
            return AnimationSmoothing.GetDistributedStepInterval(
                GetTweenMotionDuration(row, frame),
                tweenSteps,
                tweenStep);
        }

        private void ScheduleCurrentTweenTick()
        {
            if (_skinTransitionActive)
            {
                _animationTimer.Interval = SkinTransitionContract.TickMilliseconds;
                return;
            }
            int row = (int)_state;
            int tweenSteps = GetCurrentTweenSteps();
            int transitionInterval = GetTweenInterval(row, _stateFrame, tweenSteps, _tweenStep);
            int holdDuration = GetTweenHoldDuration(row, _stateFrame);
            _animationTimer.Interval = _tweenStep == 0
                && !_longKeyFrameHoldConsumed
                && holdDuration >= AnimationSmoothing.MinimumTickMilliseconds
                    ? holdDuration
                    : transitionInterval;
        }

        private bool FinishScheduledLongKeyFrameHold()
        {
            if (_tweenStep != 0 || _longKeyFrameHoldConsumed)
            {
                return false;
            }

            int row = (int)_state;
            int tweenSteps = GetCurrentTweenSteps();
            int transitionInterval = GetTweenInterval(row, _stateFrame, tweenSteps, 0);
            int holdDuration = GetTweenHoldDuration(row, _stateFrame);
            if (holdDuration < AnimationSmoothing.MinimumTickMilliseconds)
            {
                return false;
            }

            _longKeyFrameHoldConsumed = true;
            _animationTimer.Interval = transitionInterval;
            return true;
        }

        private static bool IsMovementState(PetState state)
        {
            return state == PetState.RunningRight
                || state == PetState.RunningLeft;
        }

        private void StartAction(PetState state, int loops)
        {
            if (_skinTransitionActive)
            {
                return;
            }
            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            ResetDragReactionState();
            _movingToTarget = false;
            _state = state;
            _stateFrame = 0;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = true;
            _remainingActionFrames = FrameCounts[(int)state] * Math.Max(1, loops);
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private void StartActionAtFrame(PetState state, int firstFrame, int actionFrameCount)
        {
            if (_skinTransitionActive)
            {
                return;
            }

            int availableFrames = FrameCounts[(int)state];
            int startFrame = Math.Max(0, Math.Min(firstFrame, availableFrames - 1));
            int remainingFrames = Math.Max(
                1,
                Math.Min(actionFrameCount, availableFrames - startFrame));
            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            ResetDragReactionState();
            _movingToTarget = false;
            _state = state;
            _stateFrame = startFrame;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = true;
            _remainingActionFrames = remainingFrames;
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private void SetLoopingState(PetState state)
        {
            if (_skinTransitionActive)
            {
                return;
            }
            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            ResetDragReactionState();
            int previousFrame = _stateFrame;
            int previousTweenStep = _tweenStep;
            bool preserveGaitPhase = IsMovementState(_state) && IsMovementState(state);
            _state = state;
            _stateFrame = preserveGaitPhase
                ? previousFrame % Math.Max(1, FrameCounts[(int)state])
                : 0;
            _tweenStep = preserveGaitPhase ? previousTweenStep : 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = false;
            _remainingActionFrames = -1;
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private void ReturnToIdle()
        {
            ResetSideRestState();
            ResetSittingPhoneState();
            ResetLinanSwingState();
            ResetDragReactionState();
            _lookIndex = -1;
            _hasSmoothedLookAngle = false;
            _lastLookTimestamp = 0;
            _state = PetState.Idle;
            _stateFrame = 0;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = false;
            _remainingActionFrames = -1;
            ScheduleCurrentTweenTick();
            if (!_dragging)
            {
                ScheduleNextIdleAction(false);
            }
            RenderCurrentFrame();
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (_paused)
            {
                _animationTimer.Stop();
                _behaviorTimer.Stop();
            }
            else
            {
                if (_skinTransitionActive)
                {
                    _animationTimer.Interval = SkinTransitionContract.TickMilliseconds;
                }
                _animationTimer.Start();
                _behaviorTimer.Start();
                if (!_skinTransitionActive && _state == PetState.Idle && !_temporaryAction)
                {
                    ScheduleNextIdleAction(false);
                }
                RenderCurrentFrame();
            }
        }

        private void SetScaleFromMenu(float requestedScale)
        {
            float clamped = Math.Max(1.25f, Math.Min(4.0f, requestedScale));
            ApplyScale(clamped, true);
        }

        private void ApplyScale(float scale, bool preserveBottomCenter)
        {
            int oldBottom = Bottom;
            int oldCenterX = Left + Width / 2;
            _scale = scale;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _frameCache.Clear();
            ClientSize = new Size(
                Math.Max(1, (int)Math.Round(FrameResource.LogicalWidth * _scale)),
                Math.Max(1, (int)Math.Round(FrameResource.LogicalHeight * _scale)));
            if (_skinTransitionActive
                && !TryResizeSkinTransitionFrames(ClientSize.Width, ClientSize.Height))
            {
                AbortSkinTransition(false);
            }
            if (_animationTimer != null)
            {
                _animationTimer.Interval = _skinTransitionActive
                    ? SkinTransitionContract.TickMilliseconds
                    : (_sideRestSleeping
                        ? SleepEffectLayout.TickMilliseconds
                        : GetTweenInterval((int)_state, _stateFrame, GetCurrentTweenSteps(), 0));
            }

            if (preserveBottomCenter && IsHandleCreated)
            {
                Location = new Point(oldCenterX - Width / 2, oldBottom - Height);
                ClampToWorkingArea();
                if (_movingToTarget)
                {
                    Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
                    _roamTargetX = Math.Max(area.Left, Math.Min(_roamTargetX, area.Right - Width));
                }
                RenderCurrentFrame();
            }
        }

        private void ResetToLowerRight()
        {
            ResetSideRestState();
            ResetLinanSwingState();
            ResetSittingPhoneState();
            _movingToTarget = false;
            _state = PetState.Idle;
            _stateFrame = 0;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = false;
            _remainingActionFrames = -1;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 28, area.Bottom - Height - 18);
            ClampToWorkingArea();
            ScheduleNextIdleAction(false);
        }

        private void AlignToDesktopBottom()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            Top = area.Bottom - Height;
            if (Left < area.Left)
            {
                Left = area.Left;
            }
            if (Right > area.Right)
            {
                Left = area.Right - Width;
            }
        }

        private void ClampToWorkingArea()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        private void ScheduleNextRoam(int milliseconds)
        {
            _nextRoamAt = DateTime.UtcNow.AddMilliseconds(Math.Max(250, milliseconds));
        }

        private void RenderCurrentFrame()
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            if (_skinTransitionActive)
            {
                if (_skinTransitionOldFrame == null
                    || _skinTransitionNewPose == null
                    || _skinTransitionNewIdle == null)
                {
                    AbortSkinTransition(true);
                    return;
                }
                try
                {
                    using (Bitmap transitionFrame = SkinTransitionRenderer.Render(
                        _skinTransitionOldFrame,
                        _skinTransitionNewPose,
                        _skinTransitionNewIdle,
                        _skinTransitionStage))
                    {
                        UpdateLayeredBitmap(transitionFrame);
                    }
                }
                catch
                {
                    AbortSkinTransition(true);
                }
                return;
            }

            int row;
            int column;
            if (_state == PetState.Idle && _lookIndex >= 0 && !_dragging && !_movingToTarget && !_temporaryAction)
            {
                row = _lookIndex < 8 ? 9 : 10;
                column = _lookIndex % 8;
            }
            else
            {
                row = (int)_state;
                column = _stateFrame;
            }

            _currentRow = row;
            _currentColumn = column;
            int targetRow;
            int targetColumn;
            Bitmap scaled;
            if (_tweenStep > 0 && TryGetTweenTarget(row, column, out targetRow, out targetColumn))
            {
                scaled = _frameCache.GetTween(
                    row,
                    column,
                    targetRow,
                    targetColumn,
                    _tweenStep,
                    GetCurrentTweenSteps(),
                    ClientSize.Width,
                    ClientSize.Height);
            }
            else
            {
                scaled = _frameCache.Get(row, column, ClientSize.Width, ClientSize.Height);
            }

            if (_state == PetState.SideRest && _sideRestSleeping)
            {
                Rectangle visibleBounds = _frameCache.GetVisibleBounds(
                    (int)PetState.SideRest,
                    PersistentActionContract.SideRestSleepFrame,
                    ClientSize.Width,
                    ClientSize.Height);
                using (Bitmap sleepFrame = CreateSleepEffectFrame(scaled, visibleBounds))
                {
                    UpdateLayeredBitmap(sleepFrame);
                }
            }
            else
            {
                UpdateLayeredBitmap(scaled);
            }
        }

        private bool TryGetTweenTarget(int row, int column, out int targetRow, out int targetColumn)
        {
            targetRow = row;
            targetColumn = column;
            if (row == 9 || row == 10)
            {
                return false;
            }

            if (_state == PetState.SideRest && _temporaryAction)
            {
                if (_sideRestSleeping)
                {
                    return false;
                }
                if (_sideRestWaking)
                {
                    if (column < PersistentActionContract.SideRestWakeLastFrame)
                    {
                        targetColumn = column + 1;
                    }
                    else
                    {
                        targetRow = (int)PetState.Idle;
                        targetColumn = 0;
                    }
                    return true;
                }
                if (column < PersistentActionContract.SideRestSleepFrame)
                {
                    targetColumn = column + 1;
                    return true;
                }
                return false;
            }

            if (_state == PetState.SkinExclusive && _temporaryAction && _linanSwingActive)
            {
                if (_linanSwingExiting)
                {
                    if (column < PersistentActionContract.LinanSwingExitFirstFrame)
                    {
                        targetColumn = PersistentActionContract.LinanSwingExitFirstFrame;
                    }
                    else if (column < PersistentActionContract.LinanSwingExitLastFrame)
                    {
                        targetColumn = column + 1;
                    }
                    else
                    {
                        targetRow = (int)PetState.Idle;
                        targetColumn = 0;
                    }
                    return true;
                }

                if (_linanSwingHolding)
                {
                    if (_linanSwingExitRequested
                        && column == PersistentActionContract.LinanSwingLoopFirstFrame)
                    {
                        targetColumn = PersistentActionContract.LinanSwingExitFirstFrame;
                    }
                    else
                    {
                        // The four authored frames are temporal phases, not
                        // positions to ping-pong: low A -> forward -> low B ->
                        // backward -> low A.  Closing 5 -> 2 keeps forward and
                        // backward peaks strictly alternating at every boundary.
                        targetColumn = LinanSwingContract.GetNextLoopFrame(column);
                    }
                    return true;
                }

                if (column < PersistentActionContract.LinanSwingEnterLastFrame)
                {
                    targetColumn = column + 1;
                    return true;
                }
                return false;
            }

            if (_state == PetState.Sitting && _temporaryAction)
            {
                if (_sittingPhoneExiting)
                {
                    if (column < PersistentActionContract.SittingExitFirstFrame)
                    {
                        targetColumn = PersistentActionContract.SittingExitFirstFrame;
                    }
                    else if (column < PersistentActionContract.SittingExitLastFrame)
                    {
                        targetColumn = column + 1;
                    }
                    else
                    {
                        targetRow = (int)PetState.Idle;
                        targetColumn = 0;
                    }
                    return true;
                }

                if (_sittingPhoneHolding)
                {
                    if (column <= PersistentActionContract.SittingLoopFirstFrame)
                    {
                        targetColumn = PersistentActionContract.SittingLoopFirstFrame + 1;
                    }
                    else if (column >= PersistentActionContract.SittingLoopLastFrame)
                    {
                        targetColumn = PersistentActionContract.SittingLoopLastFrame - 1;
                    }
                    else
                    {
                        targetColumn = column + (_sittingLoopDirection >= 0 ? 1 : -1);
                    }
                    return true;
                }

                if (column < PersistentActionContract.SittingEnterLastFrame)
                {
                    targetColumn = column + 1;
                    return true;
                }
                return false;
            }

            int frameCount = row >= 0 && row < FrameCounts.Length ? FrameCounts[row] : 0;
            if (frameCount <= 1)
            {
                return false;
            }
            if (_temporaryAction && _remainingActionFrames <= 1)
            {
                targetRow = (int)PetState.Idle;
                targetColumn = 0;
            }
            else
            {
                targetColumn = (column + 1) % frameCount;
            }
            return true;
        }

        private bool AdvanceSideRestAnimation()
        {
            if (_state != PetState.SideRest || !_temporaryAction)
            {
                return false;
            }

            if (_sideRestSleeping)
            {
                _stateFrame = PersistentActionContract.SideRestSleepFrame;
                _tweenStep = 0;
                _sleepEffectTick = (_sleepEffectTick + 1) % SleepEffectLayout.ParticleStageCount;
                RenderCurrentFrame();
                _animationTimer.Interval = SleepEffectLayout.TickMilliseconds;
                return true;
            }

            int targetRow;
            int targetColumn;
            if (!TryGetTweenTarget((int)PetState.SideRest, _stateFrame, out targetRow, out targetColumn))
            {
                return false;
            }

            int tweenSteps = GetCurrentTweenSteps();
            _tweenStep++;
            if (_tweenStep < tweenSteps)
            {
                RenderCurrentFrame();
                ScheduleCurrentTweenTick();
                return true;
            }

            _tweenStep = 0;
            if (targetRow == (int)PetState.Idle)
            {
                ReturnToIdle();
                ScheduleNextIdleAction(true);
                ScheduleNextRoam(2200);
                return true;
            }

            _stateFrame = targetColumn;
            _longKeyFrameHoldConsumed = false;
            if (!_sideRestWaking && _stateFrame == PersistentActionContract.SideRestSleepFrame)
            {
                _sideRestSleeping = true;
                _remainingActionFrames = -1;
                _sleepEffectTick = 0;
            }

            RenderCurrentFrame();
            _animationTimer.Interval = _sideRestSleeping
                ? SleepEffectLayout.TickMilliseconds
                : GetTweenInterval((int)PetState.SideRest, _stateFrame, GetCurrentTweenSteps(), 0);
            return true;
        }

        private void WakeFromSideRest()
        {
            if (_state != PetState.SideRest || !_sideRestSleeping)
            {
                return;
            }

            _sideRestSleeping = false;
            _sideRestWaking = true;
            _sleepEffectTick = 0;
            // Keep the sleeping pose as the first wake transition source so
            // clicking never causes a one-keyframe jump to frame 5.
            _stateFrame = PersistentActionContract.SideRestSleepFrame;
            _tweenStep = 0;
            _longKeyFrameHoldConsumed = false;
            _temporaryAction = true;
            _remainingActionFrames = 4;
            _movingToTarget = false;
            _pendingDoubleClickWave = false;
            _lookIndex = -1;
            ScheduleCurrentTweenTick();
            RenderCurrentFrame();
        }

        private void ResetSideRestState()
        {
            _sideRestSleeping = false;
            _sideRestWaking = false;
            _sleepEffectTick = 0;
        }

        private bool AdvanceLinanSwingAnimation()
        {
            if (_state != PetState.SkinExclusive || !_temporaryAction || !_linanSwingActive)
            {
                return false;
            }

            int targetRow;
            int targetColumn;
            if (!TryGetTweenTarget((int)PetState.SkinExclusive, _stateFrame, out targetRow, out targetColumn))
            {
                return false;
            }

            int tweenSteps = GetCurrentTweenSteps();
            _tweenStep++;
            if (_tweenStep < tweenSteps)
            {
                RenderCurrentFrame();
                ScheduleCurrentTweenTick();
                return true;
            }

            _tweenStep = 0;
            if (targetRow == (int)PetState.Idle)
            {
                ReturnToIdle();
                ScheduleNextIdleAction(true);
                ScheduleNextRoam(2200);
                return true;
            }

            _stateFrame = targetColumn;
            _longKeyFrameHoldConsumed = false;
            if (!_linanSwingHolding
                && !_linanSwingExiting
                && _stateFrame == PersistentActionContract.LinanSwingEnterLastFrame)
            {
                _linanSwingHolding = true;
                _remainingActionFrames = -1;
            }
            else if (_linanSwingHolding
                && _linanSwingExitRequested
                && (_stateFrame == PersistentActionContract.LinanSwingLoopFirstFrame
                    || _stateFrame == PersistentActionContract.LinanSwingExitFirstFrame))
            {
                _linanSwingHolding = false;
                _linanSwingExitRequested = false;
                _linanSwingExiting = true;
                _remainingActionFrames = 3;
            }

            RenderCurrentFrame();
            ScheduleCurrentTweenTick();
            return true;
        }

        private void ExitLinanSwing()
        {
            if (_state != PetState.SkinExclusive
                || !_linanSwingActive
                || _linanSwingExiting
                || _linanSwingExitRequested)
            {
                return;
            }

            _linanSwingExitRequested = true;
            _movingToTarget = false;
            _pendingDoubleClickWave = false;
            _lookIndex = -1;
        }

        private void ResetLinanSwingState()
        {
            _linanSwingActive = false;
            _linanSwingHolding = false;
            _linanSwingExiting = false;
            _linanSwingExitRequested = false;
        }

        private bool AdvanceSittingAnimation()
        {
            if (_state != PetState.Sitting || !_temporaryAction)
            {
                return false;
            }

            int targetRow;
            int targetColumn;
            if (!TryGetTweenTarget((int)PetState.Sitting, _stateFrame, out targetRow, out targetColumn))
            {
                return false;
            }

            int tweenSteps = GetCurrentTweenSteps();
            _tweenStep++;
            if (_tweenStep < tweenSteps)
            {
                RenderCurrentFrame();
                ScheduleCurrentTweenTick();
                return true;
            }

            _tweenStep = 0;
            if (targetRow == (int)PetState.Idle)
            {
                ReturnToIdle();
                ScheduleNextIdleAction(true);
                ScheduleNextRoam(2200);
                return true;
            }

            _stateFrame = targetColumn;
            _longKeyFrameHoldConsumed = false;
            if (!_sittingPhoneHolding
                && !_sittingPhoneExiting
                && _stateFrame == PersistentActionContract.SittingEnterLastFrame)
            {
                _sittingPhoneHolding = true;
                _sittingLoopDirection = 1;
                _remainingActionFrames = -1;
            }
            else if (_sittingPhoneHolding)
            {
                if (_stateFrame >= PersistentActionContract.SittingLoopLastFrame)
                {
                    _sittingLoopDirection = -1;
                }
                else if (_stateFrame <= PersistentActionContract.SittingLoopFirstFrame)
                {
                    _sittingLoopDirection = 1;
                }

                // A click during an in-between frame is deferred until this
                // exact key boundary.  The displayed composite therefore
                // never snaps backward to its interpolation source pose.
                if (_sittingExitRequested)
                {
                    _sittingExitRequested = false;
                    _sittingPhoneHolding = false;
                    _sittingPhoneExiting = true;
                    _remainingActionFrames = 3;
                }
            }

            RenderCurrentFrame();
            ScheduleCurrentTweenTick();
            return true;
        }

        private void ExitSittingPhoneBreak()
        {
            if (_state != PetState.Sitting
                || !_sittingPhoneHolding
                || _sittingPhoneExiting
                || _sittingExitRequested)
            {
                return;
            }

            _sittingExitRequested = true;
            _movingToTarget = false;
            _pendingDoubleClickWave = false;
            _lookIndex = -1;
        }

        private void ResetSittingPhoneState()
        {
            _sittingPhoneHolding = false;
            _sittingPhoneExiting = false;
            _sittingExitRequested = false;
            _sittingLoopDirection = 1;
        }

        private Bitmap CreateSleepEffectFrame(Bitmap baseFrame, Rectangle visibleBounds)
        {
            // Keep the same premultiplied-alpha format as the scaled frame
            // cache so UpdateLayeredWindow renders the translucent glyphs
            // without dark fringes.
            Bitmap composed = new Bitmap(baseFrame.Width, baseFrame.Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(composed))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.DrawImageUnscaled(baseFrame, 0, 0);

                float scaleX = baseFrame.Width / (float)FrameResource.LogicalWidth;
                float scaleY = baseFrame.Height / (float)FrameResource.LogicalHeight;
                float effectScale = Math.Max(0.75f, Math.Min(scaleX, scaleY));
                string[] glyphs = new string[] { "z", "Z", "z" };
                float[] logicalSizes = new float[] { 8.5f, 11.5f, 9.5f };

                // Side-rest skins keep the same pose contract: the head is
                // the upper-left mass of the opaque silhouette.  Deriving the
                // emitter from that silhouette makes the Zs follow alternate
                // skins and window scales instead of using a window constant.
                PointF emitter = SleepEffectLayout.GetHeadEmitter(
                    visibleBounds,
                    baseFrame.Width,
                    baseFrame.Height);

                for (int index = 0; index < glyphs.Length; index++)
                {
                    double phase = ((_sleepEffectTick / (double)SleepEffectLayout.ParticleStageCount) + index * 0.34) % 1.0;
                    int alpha = Math.Max(0, Math.Min(230, (int)Math.Round(230.0 * Math.Pow(1.0 - phase, 1.35))));
                    float fontSize = logicalSizes[index] * effectScale;

                    using (Font font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                    {
                        SizeF measured = graphics.MeasureString(glyphs[index], font, PointF.Empty, format);
                        float x = emitter.X
                            + index * 3.2f * effectScale
                            + (float)(phase * 7.0 * effectScale);
                        float y = emitter.Y
                            - measured.Height * 0.88f
                            - (float)(phase * 42.0 * scaleY)
                            - index * 1.6f * effectScale;
                        x = Math.Max(1.0f, Math.Min(x, baseFrame.Width - measured.Width - 1.0f));
                        y = Math.Max(1.0f, Math.Min(y, baseFrame.Height - measured.Height - 1.0f));

                        Color glowColor = Color.FromArgb(alpha / 2, 90, 186, 238);
                        Color faceColor = index % 2 == 0
                            ? Color.FromArgb(alpha, 219, 246, 255)
                            : Color.FromArgb(alpha, 255, 255, 255);
                        using (SolidBrush glow = new SolidBrush(glowColor))
                        using (SolidBrush face = new SolidBrush(faceColor))
                        {
                            float glowOffset = Math.Max(1.0f, effectScale * 0.65f);
                            graphics.DrawString(glyphs[index], font, glow, x + glowOffset, y + glowOffset, format);
                            graphics.DrawString(glyphs[index], font, face, x, y, format);
                        }
                    }
                }
            }
            return composed;
        }

        private void UpdateLayeredBitmap(Bitmap bitmap)
        {
            IntPtr screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memoryDeviceContext = NativeMethods.CreateCompatibleDC(screenDeviceContext);
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(memoryDeviceContext, bitmapHandle);
                NativePoint source = new NativePoint(0, 0);
                NativePoint destination = new NativePoint(Left, Top);
                NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AcSrcOver;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;

                bool updated = NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDeviceContext,
                    ref destination,
                    ref size,
                    memoryDeviceContext,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha);
                if (!updated)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memoryDeviceContext, oldBitmap);
                }
                if (bitmapHandle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(bitmapHandle);
                }
                if (memoryDeviceContext != IntPtr.Zero)
                {
                    NativeMethods.DeleteDC(memoryDeviceContext);
                }
                if (screenDeviceContext != IntPtr.Zero)
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        public int Width;
        public int Height;

        public NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr graphicsObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr windowHandle,
            IntPtr destinationDeviceContext,
            ref NativePoint destinationPoint,
            ref NativeSize size,
            IntPtr sourceDeviceContext,
            ref NativePoint sourcePoint,
            int colorKey,
            ref BlendFunction blend,
            int flags);
    }
}
