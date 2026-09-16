using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DdalggakBackup
{
    public static class PathSafety
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, uint count, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

        public static string ValidateName(string text)
        {
            string name = (text ?? "").Trim();
            if (name.Length == 0) name = "과검제어전";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => c < 32))
                throw new ArgumentException("이름에는 \\ / : * ? \" < > | 또는 제어 문자를 사용할 수 없습니다.");
            if (name == "." || name == ".." || name.EndsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("이름 끝에 마침표를 사용할 수 없습니다.");
            if (IsReserved(name)) throw new ArgumentException("CON, PRN, AUX, NUL, COM1, LPT1 같은 Windows 예약 이름은 사용할 수 없습니다.");
            if (name.Length + 7 + Catalog.IncompleteSuffix.Length > 255)
                throw new ArgumentException("백업 이름이 너무 깁니다. 날짜와 미완료 표식을 포함해 255자 이하여야 합니다.");
            return name;
        }

        public static string ValidateItemName(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("항목 폴더 이름을 입력해 주세요.");
            if (name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => c < 32))
                throw new ArgumentException("항목에는 폴더 이름 하나만 입력하세요. 경로 구분자나 \\ / : * ? \" < > | 문자는 사용할 수 없습니다.");
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new ArgumentException("항목 폴더 이름 끝에 마침표나 공백을 사용할 수 없습니다.");
            string devicePart = name.Split('.')[0].TrimEnd(' ');
            if (IsReserved(devicePart))
                throw new ArgumentException("Windows 예약 이름은 항목 폴더 이름으로 사용할 수 없습니다: " + name);
            if (name.Length > 255)
                throw new ArgumentException("항목 폴더 이름은 255자를 넘을 수 없습니다.");
            return name;
        }

        public static string[] ValidateItemNames(IEnumerable<string> names)
        {
            string[] result = names == null ? (string[])Catalog.Items.Clone() : names.ToArray();
            if (result.Length != Catalog.Items.Length)
                throw new ArgumentException("항목 폴더 이름은 정확히 6개를 설정해야 합니다.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < result.Length; index++)
            {
                try { ValidateItemName(result[index]); }
                catch (ArgumentException error)
                {
                    throw new ArgumentException((index + 1) + "번 항목: " + error.Message);
                }
                if (!seen.Add(result[index]))
                    throw new ArgumentException("항목 폴더 이름이 중복됩니다: " + result[index]);
            }
            return result;
        }

        private static bool IsReserved(string component)
        {
            return Regex.IsMatch(component, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static string FolderName(string text, DateTime start)
        {
            return start.ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "_" + ValidateName(text);
        }

        public static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("원본·백업 기본 경로를 설정해 주세요.");
            // Spaces are legitimate path characters. Do not silently change configured paths.
            string value = path.Replace('/', '\\');
            if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("장치 경로 대신 드라이브 전체 경로나 UNC 경로를 입력해 주세요.");
            if (!Regex.IsMatch(value, @"^[A-Za-z]:\\") && !Regex.IsMatch(value, @"^\\\\[^\\]+\\[^\\]+(?:\\|$)"))
                throw new ArgumentException(@"C:\폴더 또는 \\서버\공유\폴더 형식의 전체 경로가 필요합니다.");
            string full = Path.GetFullPath(value);
            string root = Path.GetPathRoot(full);
            foreach (string part in full.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith(".") || part.EndsWith(" ") || IsReserved(part))
                    throw new ArgumentException("Windows에서 사용할 수 없는 경로입니다: " + part);
                if (part.Length > 255) throw new PathTooLongException("경로의 폴더 이름은 255자를 넘을 수 없습니다.");
            }
            return full.Length > root.Length ? full.TrimEnd('\\') : full;
        }

        private static string NativePath(string path)
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        private static IOException NativeError(string action, string path, int code)
        {
            return new IOException(action + ": " + path + " — " + new Win32Exception(code).Message, unchecked((int)(0x80070000u | (uint)code)));
        }

        public static string Canonicalize(string path)
        {
            string current = Normalize(path);
            var missing = new Stack<string>();
            while (GetFileAttributesW(NativePath(current)) == UInt32.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 2 && error != 3) throw NativeError("실제 경로를 확인할 수 없습니다", current, error);
                string parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    throw NativeError("경로를 찾을 수 없습니다", current, error);
                missing.Push(Path.GetFileName(current));
                current = parent;
            }
            // Access 0 reads no source content and requests no write or attribute modification rights.
            using (SafeFileHandle handle = CreateFileW(NativePath(current), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw NativeError("실제 경로를 열 수 없습니다", current, Marshal.GetLastWin32Error());
                var buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length >= buffer.Capacity)
                {
                    buffer.Capacity = checked((int)length + 1);
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                }
                if (length == 0) throw NativeError("링크·정션의 실제 경로를 확인할 수 없습니다", current, Marshal.GetLastWin32Error());
                string final = buffer.ToString();
                if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final.Substring(8);
                else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final.Substring(4);
                while (missing.Count > 0) final = Path.Combine(final, missing.Pop());
                return Normalize(final);
            }
        }

        public static bool IsWithin(string candidate, string directory)
        {
            string a = Normalize(candidate).TrimEnd('\\');
            string b = Normalize(directory).TrimEnd('\\');
            return String.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static void RequireDirectory(string path, string label)
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) == 0) throw new IOException(label + "가 폴더가 아닙니다: " + path);
        }

        public static void ValidateRoots(string sourceRoot, string backupRoot, IEnumerable<string> selected)
        {
            ValidateRoots(sourceRoot, backupRoot, selected, null);
        }

        public static void ValidateRoots(string sourceRoot, string backupRoot, IEnumerable<string> selected, IEnumerable<string> configuredItems)
        {
            string[] configured = ValidateItemNames(configuredItems);
            string source = Normalize(sourceRoot), backup = Normalize(backupRoot);
            var items = (selected ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (items.Length == 0) throw new ArgumentException("백업할 항목을 하나 이상 선택해 주세요.");
            foreach (string item in items) ValidateItemName(item);
            if (items.Any(item => !configured.Contains(item, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("설정되지 않은 백업 항목입니다.");
            RequireDirectory(source, "원본 기본 경로");
            RequireDirectory(backup, "백업 기본 경로");
            EnsureSafeWrite(backup, source, configured);
            RequireWritableDirectory(backup);
            foreach (string item in items)
            {
                SourceStatus status = CheckSource(source, item);
                if (!status.Available) throw new IOException(item + ": " + status.Message);
                string parent = Path.Combine(backup, item);
                EnsureSafeWrite(parent, source, configured);
                if (File.Exists(parent)) throw new IOException("백업 항목 위치에 같은 이름의 파일이 있습니다: " + parent);
                if (Directory.Exists(parent)) RequireWritableDirectory(parent);
            }
        }

        private static void RequireWritableDirectory(string path)
        {
            // Request FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY on an existing directory.
            // This asks Windows/share ACLs for access without creating a probe file.
            using (SafeFileHandle handle = CreateFileW(NativePath(path), 0x0002 | 0x0004, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw NativeError("백업 경로에 쓰기 권한이 없습니다", path, Marshal.GetLastWin32Error());
            }
        }

        public static void EnsureSafeWrite(string path, string sourceRoot)
        {
            EnsureSafeWrite(path, sourceRoot, null);
        }

        public static void EnsureSafeWrite(string path, string sourceRoot, IEnumerable<string> configuredItems)
        {
            string[] configured = ValidateItemNames(configuredItems);
            string source = Normalize(sourceRoot);
            RequireDirectory(source, "원본 기본 경로");
            string target = Canonicalize(path);
            foreach (string item in configured.Concat(Catalog.Items).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string itemPath = Canonicalize(Path.Combine(source, item));
                if (IsWithin(target, itemPath))
                    throw new IOException("백업 위치가 원본 항목 안에 있습니다. 원본 보호를 위해 차단했습니다: " + path + " → " + itemPath);
            }
        }

        public static void EnsureOrdinarySource(string path, string itemRoot)
        {
            string root = Normalize(itemRoot), full = Normalize(path);
            if (!IsWithin(full, root)) throw new IOException("원본 항목 밖의 경로입니다: " + full);
            string current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크·정션은 따라가지 않습니다: " + current);
            string tail = full.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
            foreach (string part in tail.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("링크·정션은 따라가지 않습니다: " + current);
            }
        }

        public static void ValidateDestinationLength(string path)
        {
            string full = Normalize(path);
            // Reserve the legacy .NET directory limit rather than silently truncating paths.
            if (full.Length > 247)
                throw new PathTooLongException("대상 경로가 너무 깁니다 (최대 247자). 백업 경로나 이름을 줄여 주세요: " + full);
        }

        public static SourceStatus CheckSource(string root, string item)
        {
            try
            {
                ValidateItemName(item);
                string path = Path.Combine(Normalize(root), item);
                RequireDirectory(path, "원본 항목");
                EnsureOrdinarySource(path, path);
                using (var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) { entries.MoveNext(); }
                return new SourceStatus { Available = true, Message = "사용 가능" };
            }
            catch (Exception error) { return new SourceStatus { Available = false, Message = error.Message }; }
        }

        public static string PreviewPath(string backupRoot, string item, string folderName)
        {
            ValidateItemName(item);
            ValidateItemName(folderName);
            string parent = Path.Combine(Normalize(backupRoot), item);
            for (int i = 0; i < Int32.MaxValue; i++)
            {
                string candidate = Path.Combine(parent, folderName + (i == 0 ? "" : "_" + i.ToString("D2")));
                ValidateDestinationLength(candidate + Catalog.IncompleteSuffix);
                if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + Catalog.IncompleteSuffix) && !Directory.Exists(candidate + Catalog.IncompleteSuffix))
                    return candidate;
            }
            throw new IOException("사용 가능한 백업 번호를 찾을 수 없습니다.");
        }

        public static bool TryCreateDirectoryExclusive(string path)
        {
            if (CreateDirectoryW(NativePath(Normalize(path)), IntPtr.Zero)) return true;
            int code = Marshal.GetLastWin32Error();
            if (code == 183 || code == 80) return false;
            throw NativeError("백업 폴더를 만들 수 없습니다", path, code);
        }

        public static long? AvailableBytes(string path)
        {
            ulong available, total, free;
            if (!GetDiskFreeSpaceExW(Normalize(path), out available, out total, out free)) return null;
            return available > Int64.MaxValue ? Int64.MaxValue : (long)available;
        }
    }
}
