// ============================================================================
//  HashTool —— 桌面版 MD5 / SHA-256 批量计算工具
// ----------------------------------------------------------------------------
//  技术选型：只用 Windows / .NET Framework 自带的东西
//    * 界面   : System.Windows.Forms（无需任何第三方库）
//    * 哈希   : System.Security.Cryptography（与 Get-FileHash / certutil 同一套 API）
//    * 拖拽   : WinForms 原生 AllowDrop + DragDrop（不需要 tkinterdnd2 之类）
//    * 编译   : Windows 自带的 csc.exe（C:\Windows\Microsoft.NET\Framework64\v4.0.30319）
//    产物是几十 KB 的单文件 exe，秒开、不解压临时目录、不需要装运行时。
//
//  语法保持 C# 5 兼容（不用字符串插值 / ?. / out var），这样系统自带的老
//  csc.exe 和新版 Roslyn 都能编译。
//
//  功能：
//    拖入文件 / 文件夹、按钮选择、文件夹递归、MD5+SHA-256 同时算、
//    线程池并发（线程数可选）、可取消、列排序、关键词筛选、重复文件提示、
//    右键复制（MD5 / SHA-256 / 路径 / 文件名 / TSV / JSON）、导出 JSON、
//    重新计算、F5 刷新（重新扫描 + 重算）、大写 / 小写自由切换。
//
//  命令行（同一个源码编译出的 HashTool-cli.exe 或带 --cli 的 HashTool.exe）：
//    HashTool.exe --cli <路径...> [--json out.json] [--upper] [--quiet]
//                  [--no-recursive] [--workers N]
//    HashTool.exe --selftest
//    HashTool.exe --version
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ----------------------------------------------------------------------------
//  程序集信息：编译进 exe，在「右键 → 属性 → 详细信息」里能看到
// ----------------------------------------------------------------------------
[assembly: AssemblyTitle("HashTool —— 哈希校验工具")]
[assembly: AssemblyDescription("桌面版 MD5 / SHA-256 计算工具")]
[assembly: AssemblyProduct("HashTool")]
[assembly: AssemblyCompany("wangxiaobao")]
[assembly: AssemblyCopyright("Copyright © 2026 wangxiaobao")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace HashTool
{
    // ========================================================================
    //  常量与小工具
    // ========================================================================
    internal static class Const
    {
        public const string AppName = "HashTool";
        public const string Version = "1.1.0";
        public const string Author = "wangxiaobao";
        public const string Title = "哈希校验工具 · MD5 / SHA-256";
        public const int ChunkSize = 1024 * 1024;        // 1 MiB 分块
        public const long ProgressStep = 4L * 1024 * 1024; // 每 4 MiB 上报一次进度

        public const string StatePending = "pending";
        public const string StateRunning = "running";
        public const string StateOk = "ok";
        public const string StateError = "error";
        public const string StateCanceled = "canceled";

        public static string StateText(string state)
        {
            switch (state)
            {
                case StatePending: return "待计算";
                case StateRunning: return "计算中";
                case StateOk: return "✓ 完成";
                case StateError: return "✗ 失败";
                case StateCanceled: return "已取消";
                default: return state;
            }
        }

        /// <summary>人类可读的大小。</summary>
        public static string HumanSize(long bytes)
        {
            if (bytes < 0) return "-";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024.0 && index < units.Length - 1)
            {
                value /= 1024.0;
                index++;
            }
            if (index == 0) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[index];
        }

        public static string Hex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string JsonEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ========================================================================
    //  单个文件的数据
    // ========================================================================
    internal sealed class FileEntry
    {
        public string Path = "";
        public string Root = "";
        public string Name = "";
        public long Size;
        public DateTime Modified;
        public string Md5 = "";
        public string Sha256 = "";
        public string State = Const.StatePending;
        public string Error = "";
        public double ElapsedMs;
        public bool Duplicate;

        // 上次算成功时的"指纹"：大小 + 最后修改时间。
        // F5 刷新时用它判断文件有没有变过，没变就直接沿用上次的哈希结果。
        public long BaselineSize = -1;
        public DateTime BaselineMtime = DateTime.MinValue;

        public FileEntry(string path, string root)
        {
            Path = path;
            Root = root;
            Name = System.IO.Path.GetFileName(path);
        }

        /// <summary>有没有算过（指纹有效）。</summary>
        public bool HasBaseline()
        {
            return BaselineSize >= 0 && BaselineMtime != DateTime.MinValue;
        }

        public string ModifiedText()
        {
            if (Modified == DateTime.MinValue) return "";
            return Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public string StateText()
        {
            string text = Const.StateText(State);
            if (Duplicate && State == Const.StateOk) text += " · 重复";
            return text;
        }
    }

    // ========================================================================
    //  文件扫描：文件 / 文件夹（递归）、去重、跳过符号链接与联接点
    // ========================================================================
    internal static class Scanner
    {
        public static List<string> Collect(IEnumerable<string> roots, bool recursive)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                foreach (string file in Enumerate(root, recursive))
                {
                    if (seen.Add(file)) result.Add(file);
                }
            }
            return result;
        }

        public static IEnumerable<string> Enumerate(string root, bool recursive)
        {
            if (string.IsNullOrEmpty(root)) yield break;

            string full;
            try { full = System.IO.Path.GetFullPath(root); }
            catch (Exception) { yield break; }

            if (File.Exists(full))
            {
                yield return full;
                yield break;
            }
            if (!Directory.Exists(full)) yield break;

            if (!recursive)
            {
                string[] files;
                try { files = Directory.GetFiles(full); }
                catch (Exception) { yield break; }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsReparsePoint(files[i])) yield return files[i];
                }
                yield break;
            }

            // 手写栈式递归：可控、能跳过重解析点（避免软链接成环）
            Stack<string> pending = new Stack<string>();
            pending.Push(full);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();

                string[] subDirs = new string[0];
                try { subDirs = Directory.GetDirectories(dir); }
                catch (Exception) { }
                Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
                for (int i = subDirs.Length - 1; i >= 0; i--)
                {
                    if (!IsReparsePoint(subDirs[i])) pending.Push(subDirs[i]);
                }

                string[] files = new string[0];
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsReparsePoint(files[i])) yield return files[i];
                }
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (Exception)
            {
                return true; // 读不到属性就跳过，避免卡住或死循环
            }
        }
    }

    // ========================================================================
    //  哈希事件与计算引擎
    // ========================================================================
    internal enum EventKind { Bytes, Done, Failed, Canceled, AllDone }

    internal sealed class HashEvent
    {
        public EventKind Kind;
        public int Index;         // 对应 FileEntry 在列表中的下标
        public string Md5 = "";
        public string Sha256 = "";
        public string Error = "";
        public long Bytes;
        public double ElapsedMs;
        public DateTime Mtime;    // 读完后的最后修改时间，用作下次刷新的指纹
    }

    internal sealed class HashEngine
    {
        private readonly List<FileEntry> _entries;
        private readonly int[] _targets;
        private readonly int _workers;
        private readonly bool _wantMd5;
        private readonly bool _wantSha256;
        private readonly ConcurrentQueue<HashEvent> _events = new ConcurrentQueue<HashEvent>();
        private int _cursor = -1;
        private int _alive;
        private volatile bool _cancel;

        public HashEngine(List<FileEntry> entries, int[] targets, int workers, bool wantMd5, bool wantSha256)
        {
            _entries = entries;
            _targets = targets;
            _workers = Math.Max(1, workers);
            _wantMd5 = wantMd5;
            _wantSha256 = wantSha256;
        }

        public ConcurrentQueue<HashEvent> Events { get { return _events; } }

        public bool Canceled { get { return _cancel; } }

        public void Start()
        {
            if (_targets.Length == 0)
            {
                _events.Enqueue(new HashEvent { Kind = EventKind.AllDone });
                return;
            }
            _alive = Math.Min(_workers, _targets.Length);
            for (int i = 0; i < _alive; i++)
            {
                Thread thread = new Thread(Worker);
                thread.IsBackground = true;
                thread.Name = "hash-" + i.ToString(CultureInfo.InvariantCulture);
                thread.Start();
            }
        }

        public void Cancel()
        {
            _cancel = true;
        }

        private void Worker()
        {
            while (true)
            {
                int slot = Interlocked.Increment(ref _cursor);
                if (slot >= _targets.Length) break;

                int index = _targets[slot];
                FileEntry entry = _entries[index];

                if (_cancel)
                {
                    _events.Enqueue(new HashEvent { Kind = EventKind.Canceled, Index = index });
                    continue;
                }

                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    string md5;
                    string sha256;
                    long bytes;
                    HashOne(entry.Path, index, out md5, out sha256, out bytes);
                    HashEvent done = new HashEvent();
                    done.Kind = EventKind.Done;
                    done.Index = index;
                    done.Md5 = md5;
                    done.Sha256 = sha256;
                    done.Bytes = bytes;
                    done.ElapsedMs = watch.Elapsed.TotalMilliseconds;
                    try { done.Mtime = File.GetLastWriteTime(entry.Path); }
                    catch (Exception) { done.Mtime = entry.Modified; }
                    _events.Enqueue(done);
                }
                catch (OperationCanceledException)
                {
                    _events.Enqueue(new HashEvent { Kind = EventKind.Canceled, Index = index });
                }
                catch (Exception ex)
                {
                    HashEvent failed = new HashEvent();
                    failed.Kind = EventKind.Failed;
                    failed.Index = index;
                    failed.Error = Describe(ex);
                    _events.Enqueue(failed);
                }
            }

            if (Interlocked.Decrement(ref _alive) == 0)
            {
                _events.Enqueue(new HashEvent { Kind = EventKind.AllDone });
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "没有读取权限";
            if (ex is FileNotFoundException) return "文件不存在";
            if (ex is DirectoryNotFoundException) return "路径不存在";
            if (ex is IOException) return "读取失败：" + ex.Message;
            return ex.GetType().Name + "：" + ex.Message;
        }

        private void HashOne(string path, int index, out string md5Hex, out string sha256Hex, out long total)
        {
            md5Hex = "";
            sha256Hex = "";
            total = 0;
            long pending = 0;
            byte[] buffer = new byte[Const.ChunkSize];

            // FileShare.ReadWrite | Delete：别的程序占用着也能尽量读出来
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Const.ChunkSize, FileOptions.SequentialScan))
            using (MD5 md5 = _wantMd5 ? MD5.Create() : null)
            using (SHA256 sha256 = _wantSha256 ? SHA256.Create() : null)
            {
                while (true)
                {
                    if (_cancel) throw new OperationCanceledException();
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    if (md5 != null) md5.TransformBlock(buffer, 0, read, buffer, 0);
                    if (sha256 != null) sha256.TransformBlock(buffer, 0, read, buffer, 0);
                    total += read;
                    pending += read;
                    if (pending >= Const.ProgressStep)
                    {
                        _events.Enqueue(new HashEvent { Kind = EventKind.Bytes, Index = index, Bytes = pending });
                        pending = 0;
                    }
                }
                if (md5 != null)
                {
                    md5.TransformFinalBlock(buffer, 0, 0);
                    md5Hex = Const.Hex(md5.Hash);
                }
                if (sha256 != null)
                {
                    sha256.TransformFinalBlock(buffer, 0, 0);
                    sha256Hex = Const.Hex(sha256.Hash);
                }
            }

            if (pending > 0)
            {
                _events.Enqueue(new HashEvent { Kind = EventKind.Bytes, Index = index, Bytes = pending });
            }
        }
    }

    // ========================================================================
    //  JSON 报告
    // ========================================================================
    internal static class Report
    {
        private static bool IsDuplicate(Dictionary<string, int> counter, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            int count;
            return counter.TryGetValue(value, out count) && count > 1;
        }

        public static string Build(List<FileEntry> entries, bool upper, bool wantMd5, bool wantSha256)
        {
            // 重复判定用启用的第一个算法（都启用时用 MD5）
            Dictionary<string, int> counter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                FileEntry e = entries[i];
                if (e.State != Const.StateOk) continue;
                string value = wantMd5 ? e.Md5 : e.Sha256;
                if (string.IsNullOrEmpty(value)) continue;
                int count;
                counter.TryGetValue(value, out count);
                counter[value] = count + 1;
            }

            int ok = 0, failed = 0, canceled = 0, pending = 0, duplicateGroups = 0, duplicateFiles = 0;
            long totalBytes = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                FileEntry e = entries[i];
                if (e.State == Const.StateOk) { ok++; totalBytes += e.Size; }
                else if (e.State == Const.StateError) failed++;
                else if (e.State == Const.StateCanceled) canceled++;
                else pending++;
            }
            foreach (KeyValuePair<string, int> pair in counter)
            {
                if (pair.Value > 1) { duplicateGroups++; duplicateFiles += pair.Value; }
            }

            StringBuilder sb = new StringBuilder(entries.Count * 320 + 1024);
            sb.Append("{\r\n");
            sb.Append("  \"tool\": \"HashTool\",\r\n");
            sb.Append("  \"version\": \"").Append(Const.Version).Append("\",\r\n");
            sb.Append("  \"author\": \"").Append(Const.Author).Append("\",\r\n");
            sb.Append("  \"generated_at\": \"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append("\",\r\n");
            sb.Append("  \"hash_case\": \"").Append(upper ? "upper" : "lower").Append("\",\r\n");
            sb.Append("  \"algorithms\": [");
            if (wantMd5) sb.Append("\"md5\"");
            if (wantMd5 && wantSha256) sb.Append(", ");
            if (wantSha256) sb.Append("\"sha256\"");
            sb.Append("],\r\n");
            sb.Append("  \"summary\": {\r\n");
            sb.Append("    \"file_count\": ").Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"ok\": ").Append(ok.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"failed\": ").Append(failed.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"canceled\": ").Append(canceled.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"pending\": ").Append(pending.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"total_bytes\": ").Append(totalBytes.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"total_size_human\": \"").Append(Const.HumanSize(totalBytes)).Append("\",\r\n");
            sb.Append("    \"duplicate_groups\": ").Append(duplicateGroups.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"duplicate_files\": ").Append(duplicateFiles.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"files\": [");

            for (int i = 0; i < entries.Count; i++)
            {
                FileEntry e = entries[i];
                string md5 = upper ? e.Md5.ToUpperInvariant() : e.Md5;
                string sha256 = upper ? e.Sha256.ToUpperInvariant() : e.Sha256;
                bool duplicate = IsDuplicate(counter, wantMd5 ? e.Md5 : e.Sha256);

                sb.Append(i == 0 ? "\r\n" : ",\r\n");
                sb.Append("    {\r\n");
                sb.Append("      \"name\": \"").Append(Const.JsonEscape(e.Name)).Append("\",\r\n");
                sb.Append("      \"path\": \"").Append(Const.JsonEscape(e.Path)).Append("\",\r\n");
                sb.Append("      \"root\": \"").Append(Const.JsonEscape(e.Root)).Append("\",\r\n");
                sb.Append("      \"size\": ").Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("      \"size_human\": \"").Append(Const.HumanSize(e.Size)).Append("\",\r\n");
                sb.Append("      \"modified\": ");
                if (e.Modified == DateTime.MinValue) sb.Append("null");
                else sb.Append("\"").Append(e.Modified.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)).Append("\"");
                sb.Append(",\r\n");
                sb.Append("      \"md5\": \"").Append(Const.JsonEscape(md5)).Append("\",\r\n");
                sb.Append("      \"sha256\": \"").Append(Const.JsonEscape(sha256)).Append("\",\r\n");
                sb.Append("      \"state\": \"").Append(e.State).Append("\",\r\n");
                sb.Append("      \"state_text\": \"").Append(Const.StateText(e.State)).Append("\",\r\n");
                sb.Append("      \"error\": ");
                if (string.IsNullOrEmpty(e.Error)) sb.Append("null");
                else sb.Append("\"").Append(Const.JsonEscape(e.Error)).Append("\"");
                sb.Append(",\r\n");
                sb.Append("      \"elapsed_ms\": ").Append(e.ElapsedMs.ToString("0.##", CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("      \"duplicate_of_md5\": ").Append(duplicate ? "true" : "false").Append("\r\n");
                sb.Append("    }");
            }

            sb.Append(entries.Count == 0 ? "]\r\n" : "\r\n  ]\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        public static void Save(string path, string json)
        {
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
    }

    // ========================================================================
    //  自检（--selftest / --uitest 用；正常双击运行用不到）
    // ========================================================================
    internal static class Cli
    {
        /// <summary>
        /// 输出被重定向到管道/文件时，明确按 UTF-8 写，中文才不会变乱码；
        /// 直接显示在控制台时保持系统默认（中文控制台默认就是本地代码页）。
        /// </summary>
        public static void SetupConsoleEncoding()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    Console.SetOut(writer);
                }
            }
            catch (Exception) { }
        }

        /// <summary>自检：已知向量 + 完整读取路径 + GUI 构造 + 扫描递归。</summary>
        public static int SelfTest()
        {
            SetupConsoleEncoding();
            bool ok = true;
            // 文件名带 PID + 时间戳：同一个自检可以并发跑而不会互相撞车
            string token = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "_" +
                           DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
            string sample = System.IO.Path.Combine(Environment.CurrentDirectory, "hashtool_selftest_" + token + ".tmp");
            string empty = System.IO.Path.Combine(Environment.CurrentDirectory, "hashtool_selftest_empty_" + token + ".tmp");
            string tree = System.IO.Path.Combine(Environment.CurrentDirectory, "hashtool_selftest_tree_" + token);

            try
            {
                // 已知向量： "abc"
                File.WriteAllBytes(sample, Encoding.ASCII.GetBytes("abc"));
                List<FileEntry> entries = new List<FileEntry>();
                FileEntry entry = new FileEntry(sample, "");
                entries.Add(entry);
                HashEngine engine = new HashEngine(entries, new int[] { 0 }, 1, true, true);
                engine.Start();
                while (true)
                {
                    HashEvent ev;
                    if (!engine.Events.TryDequeue(out ev)) { Thread.Sleep(5); continue; }
                    if (ev.Kind == EventKind.Done)
                    {
                        entry.Md5 = ev.Md5;
                        entry.Sha256 = ev.Sha256;
                        entry.State = Const.StateOk;
                    }
                    if (ev.Kind == EventKind.AllDone) break;
                }

                // 已知向量：空文件
                File.WriteAllBytes(empty, new byte[0]);
                string emptyMd5, emptySha;
                using (FileStream fs = File.OpenRead(empty))
                using (MD5 md5 = MD5.Create())
                using (SHA256 sha = SHA256.Create())
                {
                    emptyMd5 = Const.Hex(md5.ComputeHash(fs));
                }
                using (FileStream fs = File.OpenRead(empty))
                using (SHA256 sha = SHA256.Create())
                {
                    emptySha = Const.Hex(sha.ComputeHash(fs));
                }

                const string abcMd5 = "900150983cd24fb0d6963f7d28e17f72";
                const string abcSha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
                const string emptyMd5Ex = "d41d8cd98f00b204e9800998ecf8427e";
                const string emptyShaEx = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

                ok = entry.Md5 == abcMd5 && entry.Sha256 == abcSha &&
                     emptyMd5 == emptyMd5Ex && emptySha == emptyShaEx;

                Console.WriteLine("HashTool " + Const.Version + " selftest");
                Console.WriteLine("runtime        : .NET Framework " + Environment.Version);
                Console.WriteLine("os             : " + Environment.OSVersion.VersionString);
                Console.WriteLine("cli_argv0      : " + Environment.GetCommandLineArgs()[0]);
                Console.WriteLine("\"abc\"     md5 : " + entry.Md5);
                Console.WriteLine("\"abc\"  sha256 : " + entry.Sha256);
                Console.WriteLine("(empty)   md5 : " + emptyMd5);
                Console.WriteLine("(empty)sha256 : " + emptySha);

                // GUI 能否构造（不显示窗口）
                bool uiOk;
                try
                {
                    using (MainForm form = new MainForm())
                    {
                        uiOk = form.Controls.Count > 0;
                    }
                }
                catch (Exception ex)
                {
                    uiOk = false;
                    Console.WriteLine("GUI 构造失败：" + ex.Message);
                }
                Console.WriteLine("gui_construct  : " + (uiOk ? "OK" : "FAILED"));
                ok = ok && uiOk;

                // 扫描递归
                Directory.CreateDirectory(System.IO.Path.Combine(tree, "sub", "deep"));
                File.WriteAllText(System.IO.Path.Combine(tree, "a.txt"), "x");
                File.WriteAllText(System.IO.Path.Combine(tree, "sub", "b.txt"), "y");
                File.WriteAllText(System.IO.Path.Combine(tree, "sub", "deep", "c.txt"), "z");
                int recursiveCount = Scanner.Collect(new string[] { tree }, true).Count;
                int flatCount = Scanner.Collect(new string[] { tree }, false).Count;
                Console.WriteLine("scan recursive : " + recursiveCount + " (期望 3)");
                Console.WriteLine("scan flat      : " + flatCount + " (期望 1)");
                ok = ok && recursiveCount == 3 && flatCount == 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("自检异常：" + ex);
                ok = false;
            }
            finally
            {
                try { if (File.Exists(sample)) File.Delete(sample); } catch (Exception) { }
                try { if (File.Exists(empty)) File.Delete(empty); } catch (Exception) { }
                try { if (Directory.Exists(tree)) Directory.Delete(tree, true); } catch (Exception) { }
            }

            Console.WriteLine("SELFTEST " + (ok ? "OK" : "FAILED"));
            return ok ? 0 : 4;
        }
    }

    // ========================================================================
    //  主窗口
    // ========================================================================
    internal sealed class MainForm : Form
    {
        // ---- 数据 ----
        private readonly List<FileEntry> _entries = new List<FileEntry>();
        private readonly Dictionary<string, int> _indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> _view = new List<int>();
        private readonly Dictionary<int, int> _viewPos = new Dictionary<int, int>();
        private readonly List<string> _roots = new List<string>();
        private readonly HashSet<string> _rootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dupCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ---- 运行时 ----
        private HashEngine _engine;
        private System.Windows.Forms.Timer _timer;
        private int _sortColumn = -1;
        private bool _sortReverse;
        private long _totalBytes;
        private long _doneBytes;
        private int _doneFiles;
        private int _jobCount;
        private Stopwatch _jobWatch;
        private volatile bool _cancelRequested;
        private string _refreshNote = "";   // 本轮刷新的统计，算完后附在状态栏里

        // ---- 控件 ----
        private Button _btnAddFiles;
        private Button _btnAddFolder;
        private Button _btnRecompute;
        private Button _btnRefresh;
        private Button _btnCancel;
        private Button _btnExport;
        private Button _btnClear;
        private CheckBox _chkRecursive;
        private CheckBox _chkIncremental;
        private ComboBox _cmbWorkers;
        private ComboBox _cmbAlgo;
        private ComboBox _cmbCopy;
        private RadioButton _radLower;
        private RadioButton _radUpper;
        private TextBox _txtFilter;
        private ListView _list;
        private TextBox _txtDetailName;
        private TextBox _txtDetailMd5;
        private TextBox _txtDetailSha;
        private Label _lblFieldMd5;
        private Label _lblFieldSha;
        private Label _lblSummary;
        private Label _lblStatus;
        private ProgressBar _progress;
        private ContextMenuStrip _menu;

        private const int ColIndex = 0, ColName = 1, ColSize = 2, ColTime = 3, ColMd5 = 4, ColSha = 5, ColState = 6, ColPath = 7;

        private bool _initializing = true;

        public MainForm()
        {
            BuildUi();
            _initializing = false;
            TrySetAppIcon();
        }

        /// <summary>窗口比屏幕还大时（小屏 + 高缩放）自动收进来，别跑到屏幕外。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int width = Math.Min(Width, area.Width - 20);
                int height = Math.Min(Height, area.Height - 20);
                if (width != Width || height != area.Height)
                {
                    Size = new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
                    Location = new Point(
                        area.Left + Math.Max(0, (area.Width - Width) / 2),
                        area.Top + Math.Max(0, (area.Height - Height) / 2));
                }
            }
            catch (Exception) { }
        }

        /// <summary>窗口/任务栏图标直接用 exe 里带的那个图标。</summary>
        private void TrySetAppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) Icon = icon;
            }
            catch (Exception) { }
        }

        // ---- 当前选项 ----

        /// <summary>是否计算 MD5。</summary>
        private bool WantMd5 { get { return _cmbAlgo.SelectedIndex != 2; } }

        /// <summary>是否计算 SHA-256。</summary>
        private bool WantSha256 { get { return _cmbAlgo.SelectedIndex != 1; } }

        /// <summary>这个文件按当前设置是否还需要（重新）计算。</summary>
        private bool IsStale(FileEntry entry)
        {
            if (entry.State != Const.StateOk) return true;
            if (!entry.HasBaseline()) return true;
            if (WantMd5 && entry.Md5.Length == 0) return true;
            if (WantSha256 && entry.Sha256.Length == 0) return true;
            return false;
        }

        // ------------------------------------------------------------ 界面搭建

        private void BuildUi()
        {
            Text = Const.Title + "  v" + Const.Version;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 按钮
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));   // 选项
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));   // 筛选
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 列表
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140F));  // 详情
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));   // 状态栏
            Controls.Add(root);

            // ---- 第一行：按钮 ----
            FlowLayoutPanel bar1 = new FlowLayoutPanel();
            bar1.Dock = DockStyle.Fill;
            bar1.Padding = new Padding(8, 6, 8, 0);
            bar1.WrapContents = false;
            root.Controls.Add(bar1, 0, 0);

            _btnAddFiles = MakeButton("添加文件", delegate { AddFiles(); });
            _btnAddFolder = MakeButton("添加文件夹", delegate { AddFolder(); });
            _btnRecompute = MakeButton("重新计算全部", delegate { RecomputeAll(); });
            _btnRefresh = MakeButton("刷新 (F5)", delegate { RefreshAll(); });
            _btnCancel = MakeButton("取消", delegate { CancelCompute(); });
            _btnExport = MakeButton("导出 JSON", delegate { ExportJson(); });
            _btnClear = MakeButton("清空列表", delegate { ClearAll(); });
            _btnCancel.Enabled = false;
            bar1.Controls.Add(_btnAddFiles);
            bar1.Controls.Add(_btnAddFolder);
            bar1.Controls.Add(_btnRecompute);
            bar1.Controls.Add(_btnRefresh);
            bar1.Controls.Add(_btnCancel);
            bar1.Controls.Add(_btnExport);
            bar1.Controls.Add(_btnClear);

            Label hint = new Label();
            hint.Text = "拖文件或文件夹进窗口即可添加 · 右键有复制菜单 · F5 刷新";
            hint.ForeColor = Color.FromArgb(110, 110, 110);
            hint.AutoSize = true;
            hint.Margin = new Padding(24, 9, 0, 0);
            bar1.Controls.Add(hint);

            // ---- 第二行：选项 ----
            FlowLayoutPanel bar2 = new FlowLayoutPanel();
            bar2.Dock = DockStyle.Fill;
            bar2.Padding = new Padding(8, 4, 8, 0);
            bar2.WrapContents = false;
            root.Controls.Add(bar2, 0, 1);

            _chkRecursive = new CheckBox();
            _chkRecursive.Text = "包含子文件夹（递归）";
            _chkRecursive.Checked = true;
            _chkRecursive.AutoSize = true;
            _chkRecursive.Margin = new Padding(0, 6, 12, 0);
            bar2.Controls.Add(_chkRecursive);

            bar2.Controls.Add(MakeLabel("线程数：", 4));
            _cmbWorkers = new ComboBox();
            _cmbWorkers.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbWorkers.Width = 70;
            _cmbWorkers.Items.AddRange(new object[] { "自动", "1", "2", "4", "8", "16" });
            _cmbWorkers.SelectedIndex = 0;
            _cmbWorkers.Margin = new Padding(0, 3, 12, 0);
            bar2.Controls.Add(_cmbWorkers);

            bar2.Controls.Add(MakeLabel("计算算法：", 4));
            _cmbAlgo = new ComboBox();
            _cmbAlgo.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAlgo.Width = 130;
            _cmbAlgo.Items.AddRange(new object[] { "MD5 + SHA-256", "仅 MD5（更快）", "仅 SHA-256" });
            _cmbAlgo.SelectedIndex = 0;
            _cmbAlgo.Margin = new Padding(0, 3, 12, 0);
            _cmbAlgo.SelectedIndexChanged += delegate { OnAlgoChanged(); };
            bar2.Controls.Add(_cmbAlgo);

            bar2.Controls.Add(MakeLabel("哈希大小写：", 4));
            _radLower = new RadioButton();
            _radLower.Text = "小写";
            _radLower.Checked = true;
            _radLower.AutoSize = true;
            _radLower.Margin = new Padding(0, 6, 4, 0);
            _radLower.CheckedChanged += delegate { OnCaseChanged(); };
            bar2.Controls.Add(_radLower);
            _radUpper = new RadioButton();
            _radUpper.Text = "大写";
            _radUpper.AutoSize = true;
            _radUpper.Margin = new Padding(0, 6, 12, 0);
            bar2.Controls.Add(_radUpper);

            // ---- 第三行：复制 / 刷新策略 / 筛选 ----
            FlowLayoutPanel bar3 = new FlowLayoutPanel();
            bar3.Dock = DockStyle.Fill;
            bar3.Padding = new Padding(8, 2, 8, 0);
            bar3.WrapContents = false;
            root.Controls.Add(bar3, 0, 2);

            bar3.Controls.Add(MakeLabel("Ctrl+C 复制：", 0));
            _cmbCopy = new ComboBox();
            _cmbCopy.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCopy.Width = 170;
            _cmbCopy.Items.AddRange(new object[] {
                "仅 MD5（默认）",
                "仅 SHA-256",
                "MD5 + SHA-256",
                "文件名",
                "完整路径",
                "整行（TSV）"
            });
            _cmbCopy.SelectedIndex = 0;
            _cmbCopy.Margin = new Padding(0, 3, 12, 0);
            bar3.Controls.Add(_cmbCopy);

            _chkIncremental = new CheckBox();
            _chkIncremental.Text = "F5 只重算变化的文件";
            _chkIncremental.Checked = true;
            _chkIncremental.AutoSize = true;
            _chkIncremental.Margin = new Padding(0, 6, 12, 0);
            bar3.Controls.Add(_chkIncremental);

            bar3.Controls.Add(MakeLabel("筛选：", 4));
            _txtFilter = new TextBox();
            _txtFilter.Width = 240;
            _txtFilter.Margin = new Padding(0, 3, 12, 0);
            _txtFilter.TextChanged += delegate { RebuildView(); };
            bar3.Controls.Add(_txtFilter);

            // ---- 第三行：列表 ----
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.VirtualMode = true;
            _list.RetrieveVirtualItem += ListRetrieveItem;
            _list.ColumnClick += ListColumnClick;
            _list.SelectedIndexChanged += delegate { UpdateDetails(); };
            _list.AllowDrop = true;
            _list.DragEnter += OnDragEnter;
            _list.DragDrop += OnDragDrop;
            _list.Columns.Add("序", 46, HorizontalAlignment.Center);
            _list.Columns.Add("文件名", 250, HorizontalAlignment.Left);
            _list.Columns.Add("大小", 92, HorizontalAlignment.Right);
            _list.Columns.Add("修改时间", 150, HorizontalAlignment.Center);
            _list.Columns.Add("MD5", 280, HorizontalAlignment.Left);
            _list.Columns.Add("SHA-256", 520, HorizontalAlignment.Left);
            _list.Columns.Add("状态", 100, HorizontalAlignment.Center);
            _list.Columns.Add("路径", 460, HorizontalAlignment.Left);
            _list.Margin = new Padding(8, 4, 8, 0);

            Panel listPanel = new Panel();
            listPanel.Dock = DockStyle.Fill;
            listPanel.Padding = new Padding(8, 4, 8, 0);
            listPanel.Controls.Add(_list);
            root.Controls.Add(listPanel, 0, 3);

            // ---- 第五行：详情 ----
            GroupBox detail = new GroupBox();
            detail.Text = "选中文件详情";
            detail.Dock = DockStyle.Fill;
            detail.Margin = new Padding(8, 6, 8, 0);
            detail.AllowDrop = true;
            detail.DragEnter += OnDragEnter;
            detail.DragDrop += OnDragDrop;
            root.Controls.Add(detail, 0, 4);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(6, 4, 6, 4);
            grid.ColumnCount = 3;
            grid.RowCount = 3;
            // 标签列按内容自适应：高 DPI 下也不会把"SHA-256"挤成两行
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            detail.Controls.Add(grid);

            _txtDetailName = new TextBox();
            _txtDetailName.ReadOnly = true;
            _txtDetailName.BorderStyle = BorderStyle.None;
            _txtDetailName.Dock = DockStyle.Fill;
            _txtDetailName.BackColor = SystemColors.Control;
            _txtDetailName.Margin = new Padding(3, 2, 3, 4);
            grid.Controls.Add(_txtDetailName, 0, 0);
            grid.SetColumnSpan(_txtDetailName, 3);

            grid.Controls.Add(MakeFieldLabel("MD5", out _lblFieldMd5), 0, 1);
            _txtDetailMd5 = MakeReadOnlyBox("Consolas");
            grid.Controls.Add(_txtDetailMd5, 1, 1);
            Button copyMd5 = MakeButton("复制", delegate { CopyValue(true, false); });
            copyMd5.Width = 80;
            grid.Controls.Add(copyMd5, 2, 1);

            grid.Controls.Add(MakeFieldLabel("SHA-256", out _lblFieldSha), 0, 2);
            _txtDetailSha = MakeReadOnlyBox("Consolas");
            grid.Controls.Add(_txtDetailSha, 1, 2);
            Button copySha = MakeButton("复制", delegate { CopyValue(false, true); });
            copySha.Width = 80;
            grid.Controls.Add(copySha, 2, 2);

            // ---- 第六行：状态栏 + 进度条 ----
            Panel status = new Panel();
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(0);
            root.Controls.Add(status, 0, 5);

            _lblSummary = new Label();
            _lblSummary.Text = "文件 0 · 完成 0 · 失败 0 · 合计 0 B";
            _lblSummary.AutoSize = true;
            _lblSummary.Location = new Point(12, 6);
            status.Controls.Add(_lblSummary);

            _lblStatus = new Label();
            _lblStatus.Text = "就绪：把文件或文件夹拖进窗口，或点击「添加文件 / 添加文件夹」。";
            _lblStatus.AutoSize = true;
            _lblStatus.ForeColor = Color.FromArgb(90, 90, 90);
            _lblStatus.Location = new Point(420, 6);
            status.Controls.Add(_lblStatus);

            _progress = new ProgressBar();
            _progress.Minimum = 0;
            _progress.Maximum = 1000;
            _progress.Value = 0;
            _progress.Dock = DockStyle.Bottom;
            _progress.Height = 14;
            status.Controls.Add(_progress);
            _progress.BringToFront();

            status.Resize += delegate
            {
                _lblStatus.Location = new Point(Math.Max(420, status.Width - _lblStatus.Width - 16), 6);
            };

            BuildContextMenu();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 100;
            _timer.Tick += delegate { PumpEvents(); };
        }

        private static Button MakeButton(string text, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(84, 28);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Click += handler;
            return button;
        }

        private static Label MakeLabel(string text, int leftMargin)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(leftMargin, 9, 2, 0);
            return label;
        }

        /// <summary>详情区左侧字段名：自适应宽度、单行、永不折行。</summary>
        private static Label MakeFieldLabel(string text, out Label reference)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(3, 8, 8, 0);
            reference = label;
            return label;
        }

        private static TextBox MakeReadOnlyBox(string fontName)
        {
            TextBox box = new TextBox();
            box.ReadOnly = true;
            box.Dock = DockStyle.Fill;
            box.Font = new Font(fontName, 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            box.Margin = new Padding(3, 3, 3, 3);
            return box;
        }

        private void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add(MakeMenuItem("复制 MD5", delegate { CopyValue(true, false); }));
            _menu.Items.Add(MakeMenuItem("复制 SHA-256", delegate { CopyValue(false, true); }));
            _menu.Items.Add(MakeMenuItem("复制 路径", delegate { CopyPath(); }));
            _menu.Items.Add(MakeMenuItem("复制 文件名", delegate { CopyName(); }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MakeMenuItem("复制选中行（TSV，可直接粘到 Excel）", delegate { CopyRowsTsv(); }));
            _menu.Items.Add(MakeMenuItem("复制选中行（JSON）", delegate { CopyRowsJson(); }));
            _menu.Items.Add(MakeMenuItem("复制全部（JSON）", delegate { CopyAllJson(); }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MakeMenuItem("重新计算选中", delegate { RecomputeSelected(); }));
            _menu.Items.Add(MakeMenuItem("移除选中", delegate { RemoveSelected(); }));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MakeMenuItem("在资源管理器中打开", delegate { OpenInExplorer(); }));
            _menu.Items.Add(MakeMenuItem("全选", delegate { SelectAll(); }));
            _menu.Opening += delegate { UpdateDetails(); };
            _list.ContextMenuStrip = _menu;
        }

        private static ToolStripMenuItem MakeMenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            return item;
        }

        // ------------------------------------------------------------ 快捷键

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { RefreshAll(); return true; }
            if (keyData == (Keys.Control | Keys.O)) { AddFiles(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.O)) { AddFolder(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { ExportJson(); return true; }
            if (keyData == (Keys.Control | Keys.A)) { SelectAll(); return true; }
            if (keyData == Keys.Delete) { RemoveSelected(); return true; }
            // 详情框里按 Ctrl+C 时让文本框自己复制
            if (keyData == (Keys.Control | Keys.C) && !(ActiveControl is TextBoxBase))
            {
                CopyForShortcut();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------ 拖拽

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0) AddRoots(paths);
        }

        // ------------------------------------------------------------ 添加 / 移除

        private void AddFiles()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择文件";
                dialog.Multiselect = true;
                dialog.Filter = "所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) AddRoots(dialog.FileNames);
            }
        }

        private void AddFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择文件夹（可勾选「包含子文件夹」决定是否递归）";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) == DialogResult.OK) AddRoots(new string[] { dialog.SelectedPath });
            }
        }

        private void AddRoots(string[] paths)
        {
            if (_cancelRequested || _engine != null)
            {
                SetStatus("正在计算中，请先等待完成或点击「取消」。");
                return;
            }
            _refreshNote = "";

            bool recursive = _chkRecursive.Checked;
            List<int> added = new List<int>();
            int skipped = 0;

            for (int i = 0; i < paths.Length; i++)
            {
                string raw = paths[i];
                if (string.IsNullOrEmpty(raw)) continue;

                string path;
                try { path = System.IO.Path.GetFullPath(raw.Trim().Trim('"')); }
                catch (Exception) { continue; }

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    SetStatus("跳过（不存在）：" + path);
                    skipped++;
                    continue;
                }
                if (!_rootKeys.Add(path)) continue;   // 同一个根只加一次
                _roots.Add(path);

                List<string> files = Scanner.Collect(new string[] { path }, recursive);
                for (int f = 0; f < files.Count; f++)
                {
                    string file = files[f];
                    int existing;
                    if (_indexByKey.TryGetValue(file, out existing))
                    {
                        _removedKeys.Remove(file);
                        continue;
                    }
                    FileEntry entry = new FileEntry(file, path);
                    _indexByKey[file] = _entries.Count;
                    _entries.Add(entry);
                    added.Add(_entries.Count - 1);
                }
                if (files.Count == 0) SetStatus("该路径下没有文件：" + path);
            }

            RebuildView();

            if (added.Count > 0)
            {
                SetStatus("已添加 " + added.Count + " 个文件，开始计算…");
                StartCompute(added);
            }
            else if (skipped == 0 && paths.Length > 0)
            {
                SetStatus("没有新增文件。");
            }
        }

        private void RemoveSelected()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中要移除的行。"); return; }

            List<string> paths = new List<string>();
            for (int i = 0; i < selected.Count; i++) paths.Add(_entries[selected[i]].Path);

            for (int i = 0; i < paths.Count; i++)
            {
                _removedKeys.Add(paths[i]);
                _rootKeys.Remove(paths[i]);
            }
            // 根路径本身被移除的话，也把根去掉
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (_removedKeys.Contains(_roots[i])) _roots.RemoveAt(i);
            }

            // 从数据结构里真正删掉（倒序删，避免下标错位）
            selected.Sort();
            for (int i = selected.Count - 1; i >= 0; i--)
            {
                int index = selected[i];
                _indexByKey.Remove(_entries[index].Path);
                _entries.RemoveAt(index);
            }
            ReindexEntries();
            RecomputeDuplicates();
            RebuildView();
            SetStatus("已移除 " + paths.Count + " 项。");
        }

        private void ReindexEntries()
        {
            _indexByKey.Clear();
            for (int i = 0; i < _entries.Count; i++) _indexByKey[_entries[i].Path] = i;
        }

        private void ClearAll()
        {
            if (_engine != null) { SetStatus("正在计算中，请先取消。"); return; }
            _entries.Clear();
            _indexByKey.Clear();
            _roots.Clear();
            _rootKeys.Clear();
            _removedKeys.Clear();
            _dupCount.Clear();
            _sortColumn = -1;
            _sortReverse = false;
            _progress.Value = 0;
            RebuildView();
            SetStatus("已清空。");
        }

        private void SelectAll()
        {
            if (_list.VirtualListSize == 0) return;
            _list.SelectedIndices.Clear();
            for (int i = 0; i < _list.VirtualListSize; i++) _list.SelectedIndices.Add(i);
        }

        // ------------------------------------------------------------ 计算

        private int WorkerCount()
        {
            string choice = _cmbWorkers.SelectedItem as string;
            int parsed;
            if (!string.IsNullOrEmpty(choice) && int.TryParse(choice, out parsed) && parsed > 0) return parsed;
            return Math.Max(1, Math.Min(8, Environment.ProcessorCount));
        }

        private void StartCompute(List<int> indexes)
        {
            if (_engine != null) { SetStatus("已有计算任务在进行中。"); return; }

            bool wantMd5 = WantMd5;
            bool wantSha256 = WantSha256;

            List<int> todo = new List<int>();
            long total = 0;
            for (int i = 0; i < indexes.Count; i++)
            {
                FileEntry entry = _entries[indexes[i]];
                try
                {
                    FileInfo info = new FileInfo(entry.Path);
                    if (!info.Exists) throw new FileNotFoundException();
                    entry.Size = info.Length;
                    entry.Modified = info.LastWriteTime;
                }
                catch (Exception ex)
                {
                    entry.State = Const.StateError;
                    entry.Error = ex is FileNotFoundException ? "文件不存在" : ex.Message;
                    entry.Md5 = "";
                    entry.Sha256 = "";
                    entry.BaselineSize = -1;
                    continue;
                }
                entry.State = Const.StatePending;
                entry.Error = "";
                if (wantMd5) entry.Md5 = "";
                if (wantSha256) entry.Sha256 = "";
                entry.ElapsedMs = 0;
                todo.Add(indexes[i]);
                total += entry.Size;
            }

            if (todo.Count == 0)
            {
                RebuildView();
                SetStatus("没有可计算的文件。");
                return;
            }

            _dupCount.Clear();
            _totalBytes = total;
            _doneBytes = 0;
            _doneFiles = 0;
            _jobCount = todo.Count;
            _jobWatch = Stopwatch.StartNew();
            _cancelRequested = false;

            _engine = new HashEngine(_entries, todo.ToArray(), WorkerCount(), wantMd5, wantSha256);
            _engine.Start();

            SetBusy(true);
            _progress.Value = 0;
            RebuildView();
            _timer.Start();
            SetStatus("计算中：0/" + todo.Count + " 个文件…");
        }

        private void RecomputeAll()
        {
            if (_entries.Count == 0) { SetStatus("列表为空，请先添加文件。"); return; }
            _refreshNote = "";
            List<int> indexes = new List<int>();
            for (int i = 0; i < _entries.Count; i++) indexes.Add(i);
            SetStatus("重新计算全部…");
            StartCompute(indexes);
        }

        private void RecomputeSelected()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中要重新计算的行。"); return; }
            _refreshNote = "";
            StartCompute(selected);
        }

        /// <summary>
        /// F5 刷新：重新扫描根路径（拾取新增 / 删除的文件）。
        /// 默认只重算"指纹变了"的文件（大小 + 最后修改时间），没变过的直接沿用上次结果；
        /// 取消勾选「F5 只重算变化的文件」则退化成全部重算。
        /// </summary>
        private void RefreshAll()
        {
            if (_engine != null) { SetStatus("正在计算中，请先取消再刷新。"); return; }
            if (_roots.Count == 0) { SetStatus("还没有添加任何文件或文件夹。"); return; }

            bool recursive = _chkRecursive.Checked;
            bool incremental = _chkIncremental.Checked;
            List<string> found = Scanner.Collect(_roots, recursive);
            HashSet<string> foundKeys = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);

            int added = 0;
            HashSet<string> newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < found.Count; i++)
            {
                string file = found[i];
                if (_indexByKey.ContainsKey(file) || _removedKeys.Contains(file)) continue;
                FileEntry entry = new FileEntry(file, RootOf(file));
                _indexByKey[file] = _entries.Count;
                _entries.Add(entry);
                newPaths.Add(file);
                added++;
            }

            int dropped = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                string path = _entries[i].Path;
                if (_rootKeys.Contains(path) || foundKeys.Contains(path)) continue;
                _indexByKey.Remove(path);
                _entries.RemoveAt(i);
                dropped++;
            }
            ReindexEntries();

            // 判断哪些文件需要重算
            List<int> recompute = new List<int>();
            int unchanged = 0;
            int changed = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                FileEntry entry = _entries[i];
                bool isNew = newPaths.Contains(entry.Path);
                if (!incremental)
                {
                    recompute.Add(i);
                    if (!isNew) changed++;
                    continue;
                }

                if (entry.State == Const.StatePending && !entry.HasBaseline())
                {
                    recompute.Add(i);   // 新加进来的
                    if (!isNew) changed++;
                    continue;
                }

                FileInfo info;
                try { info = new FileInfo(entry.Path); }
                catch (Exception) { recompute.Add(i); changed++; continue; }

                if (!info.Exists)
                {
                    recompute.Add(i);
                    changed++;
                    continue;
                }

                bool fingerprintChanged = IsStale(entry) ||
                                          info.Length != entry.BaselineSize ||
                                          info.LastWriteTime != entry.BaselineMtime;

                if (fingerprintChanged)
                {
                    recompute.Add(i);
                    if (!isNew) changed++;
                }
                else
                {
                    entry.Size = info.Length;
                    entry.Modified = info.LastWriteTime;
                    unchanged++;
                }
            }

            RebuildView();

            string prefix = "新增 " + added + " 个，变化 " + changed + " 个，移除 " + dropped + " 个，未变化 " + unchanged + " 个";
            if (recompute.Count == 0)
            {
                _refreshNote = "";
                SetStatus("刷新完成：" + prefix + "，没有需要重算的文件（全部沿用上次结果）。");
                return;
            }

            _refreshNote = prefix + "（沿用上次结果），本次重算 " + recompute.Count + " 个";
            if (incremental)
            {
                SetStatus("刷新完成：" + _refreshNote + "，开始重算…");
            }
            else
            {
                SetStatus("刷新完成：" + prefix + "，已按全量模式重算 " + recompute.Count + " 个…");
            }
            StartCompute(recompute);
        }

        private string RootOf(string path)
        {
            for (int i = 0; i < _roots.Count; i++)
            {
                string root = _roots[i];
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return root;
                if (Directory.Exists(root))
                {
                    string prefix = root.TrimEnd('\\') + "\\";
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return root;
                }
            }
            return _roots.Count > 0 ? _roots[0] : "";
        }

        private void CancelCompute()
        {
            if (_engine == null) return;
            _cancelRequested = true;
            _engine.Cancel();
            SetStatus("正在取消…");
        }

        /// <summary>界面线程：把引擎事件搬进 UI。</summary>
        private void PumpEvents()
        {
            if (_engine == null) return;

            HashEvent ev;
            int guard = 0;
            while (guard++ < 5000 && _engine.Events.TryDequeue(out ev))
            {
                if (ev.Kind == EventKind.Bytes)
                {
                    _doneBytes += ev.Bytes;
                    continue;
                }
                if (ev.Kind == EventKind.Done)
                {
                    FileEntry entry = _entries[ev.Index];
                    if (WantMd5) entry.Md5 = ev.Md5;
                    if (WantSha256) entry.Sha256 = ev.Sha256;
                    entry.Size = ev.Bytes;
                    if (ev.Mtime != DateTime.MinValue) entry.Modified = ev.Mtime;
                    entry.ElapsedMs = ev.ElapsedMs;
                    entry.State = Const.StateOk;
                    entry.Error = "";
                    // 记下指纹：下次 F5 用它判断文件有没有变过
                    entry.BaselineSize = ev.Bytes;
                    entry.BaselineMtime = ev.Mtime;
                    _doneFiles++;
                    _doneBytes += ev.Bytes;
                    InvalidateEntry(ev.Index);
                }
                else if (ev.Kind == EventKind.Failed)
                {
                    FileEntry entry = _entries[ev.Index];
                    entry.State = Const.StateError;
                    entry.Error = ev.Error;
                    entry.Md5 = "";
                    entry.Sha256 = "";
                    entry.BaselineSize = -1;   // 失败的下次一定要重试
                    entry.BaselineMtime = DateTime.MinValue;
                    _doneFiles++;
                    InvalidateEntry(ev.Index);
                }
                else if (ev.Kind == EventKind.Canceled)
                {
                    FileEntry entry = _entries[ev.Index];
                    entry.State = Const.StateCanceled;
                    entry.Md5 = "";
                    entry.Sha256 = "";
                    entry.BaselineSize = -1;
                    entry.BaselineMtime = DateTime.MinValue;
                    _doneFiles++;
                    InvalidateEntry(ev.Index);
                }
                else if (ev.Kind == EventKind.AllDone)
                {
                    FinishCompute();
                    return;
                }
            }

            if (_engine != null)
            {
                double percent = _totalBytes > 0
                    ? Math.Min(1.0, (double)_doneBytes / _totalBytes)
                    : Math.Min(1.0, (double)_doneFiles / Math.Max(1, _jobCount));
                _progress.Value = Math.Min(1000, (int)Math.Round(percent * 1000));
                SetStatus("计算中 " + _doneFiles + "/" + _jobCount + " · " + Const.HumanSize(_doneBytes) +
                          " / " + Const.HumanSize(_totalBytes) + " · " + (percent * 100).ToString("0.0", CultureInfo.InvariantCulture) +
                          "% · " + _jobWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            }
        }

        private void FinishCompute()
        {
            _timer.Stop();
            _jobWatch.Stop();
            _engine = null;
            _cancelRequested = false;
            SetBusy(false);

            int ok = 0, failed = 0, canceled = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].State == Const.StateOk) ok++;
                else if (_entries[i].State == Const.StateError) failed++;
                else if (_entries[i].State == Const.StateCanceled) canceled++;
            }

            RecomputeDuplicates();
            RebuildView();
            if (canceled == 0) _progress.Value = 1000;

            string text = "完成 " + ok + " 个";
            if (failed > 0) text += "，失败 " + failed + " 个";
            if (canceled > 0) text += "，已取消 " + canceled + " 个";
            text += "，用时 " + _jobWatch.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
            int groups = DuplicateGroupCount();
            if (groups > 0) text += "，检测到 " + groups + " 组重复（同 " + (WantMd5 ? "MD5" : "SHA-256") + "）";
            if (_refreshNote.Length > 0)
            {
                text += "。（本轮刷新：" + _refreshNote + "）";
                _refreshNote = "";
            }
            SetStatus(text + "。");
        }

        private void RecomputeDuplicates()
        {
            _dupCount.Clear();
            // 重复判定用当前启用的算法（两个都启用时用 MD5）
            bool useMd5 = WantMd5;
            for (int i = 0; i < _entries.Count; i++)
            {
                FileEntry entry = _entries[i];
                entry.Duplicate = false;
                if (entry.State != Const.StateOk) continue;
                string value = useMd5 ? entry.Md5 : entry.Sha256;
                if (value.Length == 0) continue;
                int count;
                _dupCount.TryGetValue(value, out count);
                _dupCount[value] = count + 1;
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                FileEntry entry = _entries[i];
                string value = useMd5 ? entry.Md5 : entry.Sha256;
                int count;
                if (value.Length > 0 && _dupCount.TryGetValue(value, out count) && count > 1)
                {
                    entry.Duplicate = true;
                }
            }
        }

        private int DuplicateGroupCount()
        {
            int groups = 0;
            foreach (KeyValuePair<string, int> pair in _dupCount)
            {
                if (pair.Value > 1) groups++;
            }
            return groups;
        }

        private void SetBusy(bool busy)
        {
            _btnAddFiles.Enabled = !busy;
            _btnAddFolder.Enabled = !busy;
            _btnRecompute.Enabled = !busy;
            _btnRefresh.Enabled = !busy;
            _btnClear.Enabled = !busy;
            _btnCancel.Enabled = busy;
        }

        // ------------------------------------------------------------ 列表显示

        private void RebuildView()
        {
            _view.Clear();
            string needle = _txtFilter.Text.Trim();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (needle.Length > 0)
                {
                    FileEntry entry = _entries[i];
                    if (entry.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                        entry.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                _view.Add(i);
            }

            if (_sortColumn >= 0) _view.Sort(CompareEntries);

            _viewPos.Clear();
            for (int i = 0; i < _view.Count; i++) _viewPos[_view[i]] = i;

            try { _list.VirtualListSize = _view.Count; }
            catch (Exception) { }

            _list.Invalidate();
            UpdateSummary();
            UpdateDetails();
            UpdateColumnHeaders();
        }

        private int CompareEntries(int a, int b)
        {
            FileEntry left = _entries[a];
            FileEntry right = _entries[b];
            int result;
            switch (_sortColumn)
            {
                case ColName: result = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase); break;
                case ColSize: result = left.Size.CompareTo(right.Size); break;
                case ColTime: result = left.Modified.CompareTo(right.Modified); break;
                case ColMd5: result = string.Compare(left.Md5, right.Md5, StringComparison.OrdinalIgnoreCase); break;
                case ColSha: result = string.Compare(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase); break;
                case ColState: result = string.Compare(left.StateText(), right.StateText(), StringComparison.OrdinalIgnoreCase); break;
                case ColPath: result = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase); break;
                default: result = a.CompareTo(b); break;
            }
            if (result == 0) result = a.CompareTo(b);
            return _sortReverse ? -result : result;
        }

        private void UpdateColumnHeaders()
        {
            string[] titles = { "序", "文件名", "大小", "修改时间", "MD5", "SHA-256", "状态", "路径" };
            for (int i = 0; i < titles.Length && i < _list.Columns.Count; i++)
            {
                string text = titles[i];
                if (i == _sortColumn) text += _sortReverse ? " ▼" : " ▲";
                _list.Columns[i].Text = text;
            }
        }

        private void ListColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_sortColumn == e.Column) _sortReverse = !_sortReverse;
            else { _sortColumn = e.Column; _sortReverse = false; }
            RebuildView();
        }

        private void ListRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count)
            {
                e.Item = new ListViewItem("");
                return;
            }
            int index = _view[e.ItemIndex];
            FileEntry entry = _entries[index];
            bool upper = _radUpper.Checked;

            string[] cells = new string[8];
            cells[ColIndex] = (e.ItemIndex + 1).ToString(CultureInfo.InvariantCulture);
            cells[ColName] = entry.Name;
            cells[ColSize] = Const.HumanSize(entry.Size);
            cells[ColTime] = entry.ModifiedText();
            cells[ColMd5] = !WantMd5 ? "-" : (upper ? entry.Md5.ToUpperInvariant() : entry.Md5);
            cells[ColSha] = !WantSha256 ? "-" : (upper ? entry.Sha256.ToUpperInvariant() : entry.Sha256);
            cells[ColState] = entry.StateText();
            cells[ColPath] = entry.Path;

            ListViewItem item = new ListViewItem(cells);
            item.BackColor = RowBackColor(entry, e.ItemIndex);
            item.ForeColor = RowForeColor(entry);
            item.ToolTipText = entry.Error.Length > 0 ? entry.Error : entry.Path;
            e.Item = item;
        }

        private static Color RowBackColor(FileEntry entry, int position)
        {
            if (entry.State == Const.StateError) return Color.FromArgb(253, 232, 232);
            if (entry.State == Const.StateCanceled) return Color.FromArgb(255, 246, 224);
            if (entry.State == Const.StatePending || entry.State == Const.StateRunning) return Color.FromArgb(246, 249, 252);
            if (entry.Duplicate) return Color.FromArgb(231, 246, 233);
            return position % 2 == 1 ? Color.FromArgb(248, 250, 252) : Color.White;
        }

        private static Color RowForeColor(FileEntry entry)
        {
            if (entry.State == Const.StatePending || entry.State == Const.StateRunning) return Color.FromArgb(130, 130, 130);
            if (entry.State == Const.StateError) return Color.FromArgb(160, 30, 30);
            return Color.Black;
        }

        private void InvalidateEntry(int index)
        {
            int position;
            if (!_viewPos.TryGetValue(index, out position)) return;
            if (position < 0 || position >= _list.VirtualListSize) return;
            try
            {
                Rectangle rect = _list.GetItemRect(position, ItemBoundsPortion.Entire);
                if (rect.Height > 0) _list.Invalidate(rect);
            }
            catch (Exception) { }
        }

        private void UpdateSummary()
        {
            int done = 0, failed = 0;
            long total = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                FileEntry entry = _entries[i];
                if (entry.State == Const.StateOk) done++;
                else if (entry.State == Const.StateError) failed++;
                if (entry.State == Const.StateOk) total += entry.Size;
            }
            string text = "文件 " + _entries.Count + " · 完成 " + done + " · 失败 " + failed + " · 合计 " + Const.HumanSize(total);
            int groups = DuplicateGroupCount();
            if (groups > 0) text += " · 重复 " + groups + " 组";
            if (_view.Count != _entries.Count) text += " · 显示 " + _view.Count;
            _lblSummary.Text = text;
        }

        private void OnCaseChanged()
        {
            if (_initializing) return;
            _list.Invalidate();
            UpdateDetails();
            SetStatus("已切换为" + (_radUpper.Checked ? "大写" : "小写") + "显示。");
        }

        /// <summary>切换计算算法：把缺少对应哈希的文件补算一遍。</summary>
        private void OnAlgoChanged()
        {
            if (_initializing) return;

            List<int> stale = new List<int>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (IsStale(_entries[i])) stale.Add(i);
            }
            RecomputeDuplicates();
            RebuildView();

            if (_engine != null)
            {
                SetStatus("算法已切换，等当前任务结束后请点「刷新 (F5)」补齐。");
                return;
            }
            if (stale.Count > 0)
            {
                SetStatus("算法已切换为「" + _cmbAlgo.SelectedItem + "」，补算 " + stale.Count + " 个文件…");
                StartCompute(stale);
            }
            else
            {
                SetStatus("算法已切换为「" + _cmbAlgo.SelectedItem + "」。");
            }
        }

        private void UpdateDetails()
        {
            int position = CurrentViewPosition();
            if (position < 0)
            {
                _txtDetailName.Text = "未选择文件";
                _txtDetailMd5.Text = "";
                _txtDetailSha.Text = "";
                return;
            }
            FileEntry entry = _entries[_view[position]];
            _txtDetailName.Text = entry.Name + "    " + Const.HumanSize(entry.Size) + "    " +
                                  entry.ModifiedText() + "    [" + entry.StateText() + "]" +
                                  (entry.Error.Length > 0 ? "  " + entry.Error : "");
            // 未启用的算法留空（不是 "-"），避免把占位符复制出去
            _txtDetailMd5.Text = !WantMd5 ? "" : (_radUpper.Checked ? entry.Md5.ToUpperInvariant() : entry.Md5);
            _txtDetailSha.Text = !WantSha256 ? "" : (_radUpper.Checked ? entry.Sha256.ToUpperInvariant() : entry.Sha256);
        }

        private int CurrentViewPosition()
        {
            if (_list.SelectedIndices.Count == 0) return -1;
            int position = _list.SelectedIndices[0];
            if (position < 0 || position >= _view.Count) return -1;
            return position;
        }

        private List<int> SelectedEntryIndexes()
        {
            List<int> result = new List<int>();
            for (int i = 0; i < _list.SelectedIndices.Count; i++)
            {
                int position = _list.SelectedIndices[i];
                if (position >= 0 && position < _view.Count) result.Add(_view[position]);
            }
            return result;
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
            _lblStatus.Location = new Point(Math.Max(420, _lblStatus.Parent.ClientSize.Width - _lblStatus.Width - 16), 6);
        }

        // ------------------------------------------------------------ 复制 / 导出

        private void SetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                SetStatus("已复制 " + text.Length + " 个字符到剪贴板。");
            }
            catch (Exception ex)
            {
                SetStatus("复制失败：" + ex.Message);
            }
        }

        private void CopyValue(bool wantMd5, bool wantSha)
        {
            if ((wantMd5 && !WantMd5) || (wantSha && !WantSha256))
            {
                SetStatus("当前「计算算法」里没有启用这个算法，先在工具栏选上再算一次。");
                return;
            }
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中一行。"); return; }
            bool upper = _radUpper.Checked;
            List<string> values = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                FileEntry entry = _entries[selected[i]];
                string value = wantMd5 ? entry.Md5 : entry.Sha256;
                if (value.Length == 0) continue;
                values.Add(upper ? value.ToUpperInvariant() : value);
            }
            if (values.Count == 0) { SetStatus("哈希为空（可能还没算完，或者该文件读取失败）。"); return; }
            SetClipboard(string.Join("\r\n", values.ToArray()));
        }

        /// <summary>
        /// Ctrl+C 的行为由工具栏「Ctrl+C 复制」下拉决定：
        /// 默认只复制 MD5；选「MD5 + SHA-256」时每个文件一行，两个哈希之间用制表符分隔。
        /// </summary>
        private void CopyForShortcut()
        {
            int mode = _cmbCopy.SelectedIndex;
            if (mode == 3) { CopyName(); return; }
            if (mode == 4) { CopyPath(); return; }
            if (mode == 5) { CopyRowsTsv(); return; }

            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中要复制的行。"); return; }

            bool upper = _radUpper.Checked;
            List<int> algorithms = new List<int>();
            if (mode == 0 && WantMd5) algorithms.Add(0);
            else if (mode == 1 && WantSha256) algorithms.Add(1);
            else if (mode == 2)
            {
                if (WantMd5) algorithms.Add(0);
                if (WantSha256) algorithms.Add(1);
            }
            if (algorithms.Count == 0) algorithms.Add(mode == 1 ? 1 : 0);

            StringBuilder sb = new StringBuilder();
            int rows = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                FileEntry entry = _entries[selected[i]];
                List<string> parts = new List<string>();
                for (int a = 0; a < algorithms.Count; a++)
                {
                    string value = algorithms[a] == 0 ? entry.Md5 : entry.Sha256;
                    if (value.Length == 0) continue;
                    parts.Add(upper ? value.ToUpperInvariant() : value);
                }
                if (parts.Count == 0) continue;
                if (rows > 0) sb.Append("\r\n");   // 一个文件一行，行与行换行
                sb.Append(string.Join(algorithms.Count > 1 ? "\t" : "", parts.ToArray()));
                rows++;
            }

            if (rows == 0) { SetStatus("哈希为空（可能还没算完，或者该文件读取失败）。"); return; }
            SetClipboard(sb.ToString());
            SetStatus("已复制 " + rows + " 行的" + (_cmbCopy.SelectedItem as string) + "。");
        }

        private void CopyPath()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中一行。"); return; }
            List<string> values = new List<string>();
            for (int i = 0; i < selected.Count; i++) values.Add(_entries[selected[i]].Path);
            SetClipboard(string.Join("\r\n", values.ToArray()));
        }

        private void CopyName()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中一行。"); return; }
            List<string> values = new List<string>();
            for (int i = 0; i < selected.Count; i++) values.Add(_entries[selected[i]].Name);
            SetClipboard(string.Join("\r\n", values.ToArray()));
        }

        private void CopyRowsTsv()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中要复制的行。"); return; }
            bool upper = _radUpper.Checked;
            StringBuilder sb = new StringBuilder();
            sb.Append("文件名\t大小\t修改时间\t");
            if (WantMd5) sb.Append("MD5\t");
            if (WantSha256) sb.Append("SHA-256\t");
            sb.Append("状态\t路径\r\n");
            for (int i = 0; i < selected.Count; i++)
            {
                FileEntry entry = _entries[selected[i]];
                sb.Append(entry.Name).Append('\t');
                sb.Append(Const.HumanSize(entry.Size)).Append('\t');
                sb.Append(entry.ModifiedText()).Append('\t');
                if (WantMd5) sb.Append(upper ? entry.Md5.ToUpperInvariant() : entry.Md5).Append('\t');
                if (WantSha256) sb.Append(upper ? entry.Sha256.ToUpperInvariant() : entry.Sha256).Append('\t');
                sb.Append(entry.StateText()).Append('\t');
                sb.Append(entry.Path).Append("\r\n");
            }
            SetClipboard(sb.ToString());
            SetStatus("已复制 " + selected.Count + " 行（TSV）。");
        }

        private List<FileEntry> EntriesOf(List<int> indexes)
        {
            List<FileEntry> list = new List<FileEntry>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++) list.Add(_entries[indexes[i]]);
            return list;
        }

        private void CopyRowsJson()
        {
            List<int> selected = SelectedEntryIndexes();
            if (selected.Count == 0) { SetStatus("请先选中要复制的行。"); return; }
            SetClipboard(Report.Build(EntriesOf(selected), _radUpper.Checked, WantMd5, WantSha256));
            SetStatus("已复制 " + selected.Count + " 行的 JSON。");
        }

        private void CopyAllJson()
        {
            if (_entries.Count == 0) { SetStatus("列表为空。"); return; }
            SetClipboard(Report.Build(_entries, _radUpper.Checked, WantMd5, WantSha256));
            SetStatus("已复制全部 " + _entries.Count + " 条的 JSON。");
        }

        private void ExportJson()
        {
            if (_entries.Count == 0) { SetStatus("列表为空，没有可导出的内容。"); return; }
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出 JSON";
                dialog.Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                dialog.FileName = "hash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Report.Save(dialog.FileName, Report.Build(_entries, _radUpper.Checked, WantMd5, WantSha256));
                    SetStatus("已导出 " + _entries.Count + " 条记录到 " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "导出 JSON", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("导出失败：" + ex.Message);
                }
            }
        }

        private void OpenInExplorer()
        {
            int position = CurrentViewPosition();
            if (position < 0) { SetStatus("请先选中一行。"); return; }
            string path = _entries[_view[position]].Path;
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else
                {
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                    else SetStatus("路径已不存在：" + path);
                }
            }
            catch (Exception ex)
            {
                SetStatus("无法打开：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 自动化测试钩子
        // 给 --uitest 用：不开对话框，但走的是和手动操作完全一样的代码路径。

        public int EntryCount { get { return _entries.Count; } }

        public bool IsComputing { get { return _engine != null; } }

        public string LastStatus { get { return _lblStatus.Text; } }

        public void AddPathsForTest(string[] paths) { AddRoots(paths); }

        public void SetUpperCase(bool upper) { _radUpper.Checked = upper; }

        public void SetIncremental(bool incremental) { _chkIncremental.Checked = incremental; }

        public void SetAlgorithm(int index) { _cmbAlgo.SelectedIndex = index; }

        public void SetCopyMode(int index) { _cmbCopy.SelectedIndex = index; }

        public void CopyForShortcutForTest() { CopyForShortcut(); }

        public void SelectAllForTest() { SelectAll(); }

        /// <summary>测试/截图用：选中某一行并让它可见。</summary>
        public void SelectRowForTest(int viewPosition)
        {
            try
            {
                _list.SelectedIndices.Clear();
                if (viewPosition >= 0 && viewPosition < _list.VirtualListSize)
                {
                    _list.SelectedIndices.Add(viewPosition);
                    _list.EnsureVisible(viewPosition);
                }
                _list.Focus();
                UpdateDetails();
                _list.Invalidate();
            }
            catch (Exception) { }
        }

        /// <summary>测试用：读取剪贴板文本（读不到返回空串）。</summary>
        public string ClipboardTextForTest()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// 详情区布局自检：字段名必须单行不折行，SHA-256 的值框要放得下 64 个字符。
        /// （高 DPI 下标签被挤成两行就是这个自检要拦的问题。）
        /// </summary>
        public bool DetailLayoutOk()
        {
            return DetailLayoutDiagnostics().IndexOf("OK") >= 0;
        }

        public string DetailLayoutDiagnostics()
        {
            string result = "";
            bool ok = true;

            if (_lblFieldSha != null && _lblFieldSha.Font != null)
            {
                double lines = (double)_lblFieldSha.Height / Math.Max(1, _lblFieldSha.Font.Height);
                if (lines >= 1.6) ok = false;
                result += "SHA-256 标签 " + _lblFieldSha.Height + "px/" + _lblFieldSha.Font.Height +
                          "px 字号（" + lines.ToString("0.0", CultureInfo.InvariantCulture) + " 行）";
            }
            if (_lblFieldMd5 != null && _lblFieldMd5.Font != null)
            {
                double lines = (double)_lblFieldMd5.Height / Math.Max(1, _lblFieldMd5.Font.Height);
                if (lines >= 1.6) ok = false;
            }

            if (_txtDetailSha != null)
            {
                int need = TextRenderer.MeasureText(new string('0', 64), _txtDetailSha.Font).Width;
                int have = _txtDetailSha.ClientSize.Width;
                if (have < need) ok = false;
                result += "；SHA-256 值框 " + have + "px / 需要 " + need + "px";
            }

            return (ok ? "OK：" : "不合格：") + result;
        }

        public void RefreshForTest() { RefreshAll(); }

        /// <summary>测试用：当前所有条目的 "名字=md5/sha256" 快照。</summary>
        public string Snapshot()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _entries.Count; i++)
            {
                FileEntry e = _entries[i];
                sb.Append(e.Name).Append('=').Append(e.Md5).Append('/').Append(e.Sha256).Append(';');
            }
            return sb.ToString();
        }

        public bool ExportTo(string path)
        {
            try
            {
                Report.Save(path, Report.Build(_entries, _radUpper.Checked, WantMd5, WantSha256));
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("导出失败：" + ex.Message);
                return false;
            }
        }
    }

    // ========================================================================
    //  入口
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0) Cli.SetupConsoleEncoding();

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--version")
                    {
                        Console.WriteLine(Const.AppName + " " + Const.Version + "   by " + Const.Author);
                        return 0;
                    }
                    if (args[i] == "--selftest") return Cli.SelfTest();
                    if (args[i] == "--uitest" && i + 1 < args.Length) return RunUiTest(args[i + 1]);
                    if (args[i] == "--screenshot" && i + 1 < args.Length) return RunScreenshot(args[i + 1]);
                    if (args[i] == "--help" || args[i] == "-h")
                    {
                        Console.WriteLine(Const.AppName + " " + Const.Version + " —— 桌面版 MD5 / SHA-256 计算工具");
                        Console.WriteLine("作者：" + Const.Author);
                        Console.WriteLine("直接双击运行即可（也可以把文件 / 文件夹拖到 exe 上）。");
                        Console.WriteLine("自检：  HashTool.exe --selftest");
                        Console.WriteLine("界面自测：HashTool.exe --uitest <输出文件前缀>");
                        return 0;
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static bool IsHex(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>造一个指定大小的样例文件（内容随机，保证哈希不重复）。</summary>
        private static void MakeSampleFile(string path, int size, byte seed)
        {
            byte[] buffer = new byte[64 * 1024];
            Random random = new Random(seed);
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                int left = size;
                while (left > 0)
                {
                    random.NextBytes(buffer);
                    int chunk = Math.Min(left, buffer.Length);
                    stream.Write(buffer, 0, chunk);
                    left -= chunk;
                }
            }
        }

        /// <summary>
        /// --screenshot &lt;输出.png&gt;
        /// 造一批样例文件、算完、选中第一行，然后把窗口画面存成 PNG（README 用的就是这张图）。
        /// </summary>
        private static int RunScreenshot(string outPath)
        {
            Cli.SetupConsoleEncoding();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string tree = System.IO.Path.Combine(Environment.CurrentDirectory,
                "hashtool_shot_tree_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            try
            {
                if (Directory.Exists(tree)) Directory.Delete(tree, true);
                Directory.CreateDirectory(tree);
                MakeSampleFile(System.IO.Path.Combine(tree, "HashTool-setup.exe"), 2 * 1024 * 1024 + 340 * 1024, 11);
                MakeSampleFile(System.IO.Path.Combine(tree, "产品说明书.pdf"), 486 * 1024, 22);
                MakeSampleFile(System.IO.Path.Combine(tree, "客户名单.xlsx"), 96 * 1024, 33);
                MakeSampleFile(System.IO.Path.Combine(tree, "客户名单_备份.xlsx"), 96 * 1024, 33); // 与上一个同内容，用来展示重复高亮
                MakeSampleFile(System.IO.Path.Combine(tree, "发布日志.txt"), 12 * 1024, 44);
            }
            catch (Exception ex)
            {
                Console.WriteLine("准备截图素材失败：" + ex.Message);
                return 5;
            }

            MainForm form = new MainForm();
            int result = 9;
            int step = 0;
            int settle = 0;
            bool waiting = false;

            System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
            watch.Interval = 150;

            watch.Tick += delegate
            {
                if (waiting)
                {
                    if (form.IsComputing) return;
                    waiting = false;
                }

                if (step == 0)
                {
                    form.AddPathsForTest(new string[] { tree });
                    waiting = true;
                    step = 1;
                    return;
                }

                if (step == 1)
                {
                    settle++;
                    if (settle < 6) return;        // 等界面画好
                    form.SelectRowForTest(0);
                    settle = 0;
                    step = 2;
                    return;
                }

                if (step == 2)
                {
                    settle++;
                    if (settle < 4) return;
                    watch.Stop();
                    try
                    {
                        using (Bitmap bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine("[screenshot] 已保存 " + outPath);
                        result = 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[screenshot] 失败：" + ex.Message);
                        result = 2;
                    }
                    form.Close();
                    return;
                }
            };

            form.Shown += delegate { watch.Start(); };
            Application.Run(form);

            try { if (Directory.Exists(tree)) Directory.Delete(tree, true); } catch (Exception) { }
            Console.WriteLine("SCREENSHOT " + (result == 0 ? "OK" : "FAILED"));
            return result;
        }

        /// <summary>
        /// --uitest &lt;输出前缀&gt;
        /// 全自动跑一遍界面链路，顺带验证「F5 只重算变化的文件」：
        ///   1) 造一棵临时目录（含重复文件）→ 添加 → 自动计算
        ///   2) 立刻 F5（没有变化）→ 应该一个都不重算
        ///   3) 改一个文件的内容 → F5 → 应该只重算 1 个
        ///   4) 加一个新文件 → F5 → 应该只新增 1 个
        ///   5) 导出小写 / 大写两份 JSON，清掉临时目录
        /// 每步的状态都会打印出来（用 HashTool-cli.exe 跑就能看到）。
        /// </summary>
        private static int RunUiTest(string outPrefix)
        {
            Cli.SetupConsoleEncoding();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string tree = System.IO.Path.Combine(Environment.CurrentDirectory,
                "hashtool_uitest_tree_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            try
            {
                if (Directory.Exists(tree)) Directory.Delete(tree, true);
                Directory.CreateDirectory(System.IO.Path.Combine(tree, "sub"));
                File.WriteAllText(System.IO.Path.Combine(tree, "one.txt"), "hello hashtool");
                File.WriteAllText(System.IO.Path.Combine(tree, "sub", "two.txt"), "hello hashtool"); // 与 one.txt 同内容
                File.WriteAllText(System.IO.Path.Combine(tree, "sub", "three.bin"), "different content");
            }
            catch (Exception ex)
            {
                Console.WriteLine("准备测试目录失败：" + ex.Message);
                return 5;
            }

            MainForm form = new MainForm();
            int result = 9;
            int step = 0;
            int ticks = 0;
            bool waiting = false;
            bool failed = false;
            List<string> log = new List<string>();

            System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
            watch.Interval = 200;

            Action<string> note = delegate(string message)
            {
                log.Add(message);
                Console.WriteLine("[uitest] " + message);
            };

            Action<bool, string> check = delegate(bool condition, string message)
            {
                if (!condition) failed = true;
                note((condition ? "PASS  " : "FAIL  ") + message);
            };

            watch.Tick += delegate
            {
                if (waiting)
                {
                    ticks++;
                    if (form.IsComputing) { if (ticks > 300) { failed = true; watch.Stop(); form.Close(); } return; }
                    waiting = false;
                }
                ticks = 0;

                switch (step)
                {
                    case 0:
                        note("添加测试目录：" + tree);
                        form.AddPathsForTest(new string[] { tree });
                        waiting = true;
                        step = 1;
                        return;

                    case 1:
                        check(form.EntryCount == 3, "递归扫描到 3 个文件（实际 " + form.EntryCount + "）");
                        check(form.LastStatus.IndexOf("完成 3 个") >= 0, "首次计算全部完成：" + form.LastStatus);
                        note("F5（没有任何改动）");
                        form.RefreshForTest();
                        waiting = true;
                        step = 2;
                        return;

                    case 2:
                        check(form.LastStatus.IndexOf("没有需要重算") >= 0,
                              "无变化时 F5 不重算：" + form.LastStatus);
                        note("改一个文件的内容，再 F5");
                        File.AppendAllText(System.IO.Path.Combine(tree, "one.txt"), " + changed");
                        form.RefreshForTest();
                        waiting = true;
                        step = 3;
                        return;

                    case 3:
                        check(form.LastStatus.IndexOf("变化 1 个") >= 0, "只重算变化的 1 个文件：" + form.LastStatus);
                        check(form.LastStatus.IndexOf("未变化 2 个") >= 0, "另外 2 个沿用上次结果：" + form.LastStatus);
                        note("新增一个文件，再 F5");
                        File.WriteAllText(System.IO.Path.Combine(tree, "four.txt"), "brand new");
                        form.RefreshForTest();
                        waiting = true;
                        step = 4;
                        return;

                    case 4:
                        check(form.LastStatus.IndexOf("新增 1 个") >= 0, "F5 拾取到新增文件：" + form.LastStatus);
                        check(form.EntryCount == 4, "文件数变成 4（实际 " + form.EntryCount + "）");
                        note("测试 Ctrl+C 复制选项");
                        form.SelectAllForTest();
                        form.SetCopyMode(0);                 // 默认：仅 MD5
                        form.CopyForShortcutForTest();
                        string md5Text = form.ClipboardTextForTest();
                        string[] md5Lines = md5Text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                        check(md5Lines.Length == 4 && md5Lines[0].Length == 32 && IsHex(md5Lines[0]),
                              "Ctrl+C 默认只复制 MD5（4 行 × 32 位十六进制）");
                        form.SetCopyMode(2);                 // MD5 + SHA-256
                        form.CopyForShortcutForTest();
                        string bothText = form.ClipboardTextForTest();
                        string[] bothLines = bothText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                        check(bothLines.Length == 4, "MD5 + SHA-256 每个文件一行（实际 " + bothLines.Length + " 行）");
                        check(bothLines[0].IndexOf('\t') == 32, "同一行内用制表符分隔两个哈希");
                        check(bothLines[0].Replace("\t", "").Length == 96, "一行里有 32+64 位哈希");
                        form.SetCopyMode(5);                 // 整行 TSV
                        form.CopyForShortcutForTest();
                        check(form.ClipboardTextForTest().StartsWith("文件名\t"), "整行 TSV 模式带表头");
                        check(form.DetailLayoutOk(), "详情区布局（" + form.DetailLayoutDiagnostics() + "）");
                        note("导出小写 / 大写 JSON");
                        bool lowerOk = form.ExportTo(outPrefix + ".json");
                        form.SetUpperCase(true);
                        bool upperOk = form.ExportTo(outPrefix + ".upper.json");
                        check(lowerOk && upperOk, "两份 JSON 都写出成功");
                        result = (!failed && lowerOk && upperOk) ? 0 : 2;
                        watch.Stop();
                        form.Close();
                        return;
                }
            };

            form.Shown += delegate
            {
                watch.Start();
            };

            Application.Run(form);

            try { if (Directory.Exists(tree)) Directory.Delete(tree, true); } catch (Exception) { }
            Console.WriteLine("UITEST " + (result == 0 ? "OK" : "FAILED"));
            return result;
        }
    }
}
