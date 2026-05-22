using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using Microsoft.Extensions.Configuration;

namespace PhotoMetadataManager;

class Program
{
    static void Main(string[] _)
    {
        // 解决 Windows 控制台输出中文乱码的问题
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            CreateDefaultConfig(configPath);
            Console.WriteLine("[提示] 已自动创建默认的 appsettings.json 配置文件，请根据需要调整后重新运行。");
            PauseAndExit();
            return;
        }

        // 1. 读取配置
        AppConfig config;
        try
        {
            var builder = new ConfigurationBuilder().AddJsonFile(
                "appsettings.json",
                optional: false,
                reloadOnChange: true
            );
            var root = builder.Build();
            config = root.Get<AppConfig>() ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 读取配置文件失败: {ex.Message}");
            PauseAndExit();
            return;
        }

        Console.WriteLine("=======================================================");
        Console.WriteLine("              图片元数据无损清理工具");
        Console.WriteLine("=======================================================\n");

        // 2. 运行时交互输入逻辑（若直接回车则读取配置文件中的默认值）

        // 2.1 输入文件夹路径
        string? inputFolder = null;
        bool validFolder = false;
        while (!validFolder)
        {
            Console.Write($"请输入要扫描的文件夹路径 (直接回车默认为: {config.Scan.FolderPath}): ");
            inputFolder = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(inputFolder))
            {
                inputFolder = config.Scan.FolderPath;
            }

            if (System.IO.Directory.Exists(inputFolder))
            {
                config.Scan.FolderPath = inputFolder;
                validFolder = true;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ 路径不存在或无效: \"{inputFolder}\"，请重新输入！");
                Console.ResetColor();
            }
        }

        // 2.2 输入文件名过滤条件
        Console.Write($"请输入文件名过滤条件 (直接回车默认为: {config.Scan.Filter}): ");
        string? inputFilter = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(inputFilter))
        {
            config.Scan.Filter = inputFilter;
        }

        // 2.3 输入是否扫描子目录
        string defaultRecurseStr = config.Scan.Recurse ? "y" : "n";
        Console.Write($"是否递归检查子文件夹？[y/n] (直接回车默认为: {defaultRecurseStr}): ");
        string? inputRecurse = Console.ReadLine()?.Trim().ToLower();
        if (!string.IsNullOrEmpty(inputRecurse))
        {
            if (inputRecurse == "y" || inputRecurse == "yes")
            {
                config.Scan.Recurse = true;
            }
            else if (inputRecurse == "n" || inputRecurse == "no")
            {
                config.Scan.Recurse = false;
            }
        }

        Console.WriteLine("\n-------------------------------------------------------");
        Console.WriteLine($"正在扫描目录: {config.Scan.FolderPath}");
        Console.WriteLine($"过滤条件: {config.Scan.Filter} | 包含子目录: {config.Scan.Recurse}");
        Console.WriteLine("-------------------------------------------------------\n");

        // 3. 检查并释放内嵌的 ExifTool
        string? exifToolPath = EnsureExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            Console.WriteLine("[严重错误] 无法获取 ExifTool。若要清理数据，请将 exiftool.exe 放在程序同目录下。");
            PauseAndExit();
            return;
        }

        if (!System.IO.Directory.Exists(config.Scan.FolderPath))
        {
            Console.WriteLine($"[错误] 扫描路径不存在: {config.Scan.FolderPath}");
            PauseAndExit();
            return;
        }

        // 3. 扫描文件
        Console.WriteLine($"\n开始扫描目录: {config.Scan.FolderPath}");
        Console.WriteLine($"过滤条件: {config.Scan.Filter} | 包含子目录: {config.Scan.Recurse}\n");

        string[] files;
        try
        {
            var option = config.Scan.Recurse
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            files = System.IO.Directory.GetFiles(
                config.Scan.FolderPath,
                config.Scan.Filter,
                option
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 无法读取文件夹: {ex.Message}");
            PauseAndExit();
            return;
        }

        // 4. 打印表头
        int totalWidth = config.Columns.Sum(c => c.Width) + (config.Columns.Count * 3) - 1;
        foreach (var col in config.Columns)
        {
            Console.Write(StringFormatter.GetFormattedString(col.DisplayName, col.Width));
            Console.Write(" | ");
        }
        Console.WriteLine();
        Console.WriteLine(new string('-', totalWidth));

        // 保存带有敏感信息（需清理的目标）的文件队列
        var targetFilesList = new List<TargetFile>();
        int scannedCount = 0;

        foreach (var file in files)
        {
            // 仅扫描常见图片格式
            string ext = Path.GetExtension(file);
            if (!Regex.IsMatch(ext, @"^\.(jpg|jpeg|png|tif|tiff)$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            scannedCount++;
            var rowData = new Dictionary<string, string>();
            bool hasGPS = false;

            // 统一读取图片元数据
            List<MetadataExtractor.Directory>? directories = null;
            try
            {
                directories = ImageMetadataReader.ReadMetadata(file).ToList();
            }
            catch
            { /* 忽略损坏的图片 */
            }

            // 解析每一列的数据
            foreach (var col in config.Columns)
            {
                rowData[col.Name] = ExifHelper.GetExifValue(
                    file,
                    col.ExifTag,
                    directories,
                    out bool fileHasGPS
                );
                if (fileHasGPS)
                    hasGPS = true;
            }

            // 判断是否包含配置中需要被清理的目标数据
            bool fileHasTargetsToClear = false;
            var accumulatedClearCommands = new List<string>();

            foreach (var col in config.Columns)
            {
                if (col.IsClearTarget)
                {
                    string val = rowData[col.Name];
                    if (val != "-" && val != "无" && !string.IsNullOrWhiteSpace(val))
                    {
                        fileHasTargetsToClear = true;
                        accumulatedClearCommands.AddRange(col.ClearCommands);
                    }
                }
            }

            // 打印当前行数据
            foreach (var col in config.Columns)
            {
                string rawValue = rowData[col.Name];
                string formatted = StringFormatter.GetFormattedString(rawValue, col.Width);

                // 如果当前列是被清理的目标，且存在值，则标红高亮
                if (
                    col.IsClearTarget
                    && rawValue != "-"
                    && rawValue != "无"
                    && !string.IsNullOrWhiteSpace(rawValue)
                )
                {
                    Console.BackgroundColor = ConsoleColor.Red;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write(formatted);
                    Console.ResetColor();
                }
                else if (rawValue == "无" || rawValue == "-")
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(formatted);
                    Console.ResetColor();
                }
                else
                {
                    Console.Write(formatted);
                }
                Console.Write(" | ");
            }
            Console.WriteLine();

            // 记录进入阶段二
            if (fileHasTargetsToClear)
            {
                targetFilesList.Add(
                    new TargetFile
                    {
                        FilePath = file,
                        RowData = rowData,
                        ClearCommands = accumulatedClearCommands.Distinct().ToList()
                    }
                );
            }
        }

        Console.WriteLine(new string('-', totalWidth));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"扫描完成！共检查 {scannedCount} 个图片文件，其中发现 {targetFilesList.Count} 个文件需要被清理。`n"
        );
        Console.ResetColor();

        // 5. 阶段二：清理敏感数据
        if (targetFilesList.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("=======================================================");
            Console.WriteLine($"开始进入数据无损清理阶段 (共 {targetFilesList.Count} 个文件)...");
            Console.WriteLine("=======================================================\n");
            Console.ResetColor();

            int clearedCount = 0;

            foreach (var item in targetFilesList)
            {
                // 重新渲染这一行
                foreach (var col in config.Columns)
                {
                    string rawValue = item.RowData[col.Name];
                    string formatted = StringFormatter.GetFormattedString(rawValue, col.Width);

                    if (
                        col.IsClearTarget
                        && rawValue != "-"
                        && rawValue != "无"
                        && !string.IsNullOrWhiteSpace(rawValue)
                    )
                    {
                        Console.BackgroundColor = ConsoleColor.Red;
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write(formatted);
                        Console.ResetColor();
                    }
                    else if (rawValue == "无" || rawValue == "-")
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(formatted);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.Write(formatted);
                    }
                    Console.Write(" | ");
                }

                // 强制换行，让交互提示在表格下方，保障长表格的美观性
                Console.WriteLine();

                // 动态统计当前文件有哪些“敏感列”即将被处理
                var clearedColumns = new List<string>();
                foreach (var col in config.Columns)
                {
                    if (col.IsClearTarget)
                    {
                        string val = item.RowData[col.Name];
                        if (val != "-" && val != "无" && !string.IsNullOrWhiteSpace(val))
                        {
                            clearedColumns.Add(col.DisplayName);
                        }
                    }
                }

                string targetNames = string.Join(", ", clearedColumns);
                bool processed = false;
                string promptText = $"    -> 发现敏感属性 [{targetNames}]，是否清空该文件相关属性？[y/n/o查看] 默认n: ";

                while (!processed)
                {
                    Console.Write(promptText);
                    string? choice = Console.ReadLine()?.Trim().ToLower();

                    if (choice == "o")
                    {
                        try
                        {
                            Process.Start(
                                new ProcessStartInfo(item.FilePath) { UseShellExecute = true }
                            );
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    -> [错误] 无法打开图片: {ex.Message}");
                        }
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("    -> ");
                        Console.ResetColor();
                        promptText = "已打开图片，请确认是否清空该文件相关属性？[y/n/o] 默认n: ";
                    }
                    else if (choice == "y")
                    {
                        // 实时显示当前正在清理的具体列名
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        foreach (var colName in clearedColumns)
                        {
                            Console.WriteLine($"    -> 正在清理: {colName}");
                        }
                        Console.ResetColor();

                        bool success = ClearFileMetadata(
                            exifToolPath,
                            item.FilePath,
                            item.ClearCommands
                        );
                        if (success)
                        {
                            clearedCount++;
                        }
                        processed = true;
                    }
                    else
                    {
                        processed = true;
                    }
                }

                // 每次循环结束输出一个空行作为文件之间的间隔，视觉体验更佳
                Console.WriteLine();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n清理结束！本次共成功抹除了 {clearedCount} 个文件的敏感配置数据。");
            Console.ResetColor();
        }

        PauseAndExit();
    }

    private static bool ClearFileMetadata(
        string exifToolPath,
        string filePath,
        List<string> clearCommands
    )
    {
        try
        {
            var argsList = new List<string>(clearCommands);
            argsList.Add("-P"); // 保留原始文件修改时间
            argsList.Add("-overwrite_original"); // 直接覆盖不留备份
            argsList.Add("-m"); // 忽视轻微警告
            argsList.Add(filePath);

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in argsList)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            process.WaitForExit();
            string output = process.StandardOutput.ReadToEnd();

            if (
                process.ExitCode == 0
                && (
                    output.Contains("1 image files updated")
                    || output.Contains("1 image files unchanged")
                )
            )
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("    -> [已清空] 配置的数据已被无损抹除（保留了其它未配置的数据）");
                Console.ResetColor();
                return true;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    -> [失败] 清理执行出错，输出: {output}");
                Console.ResetColor();
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    -> [错误] 执行清理时崩溃: {ex.Message}");
            return false;
        }
    }

    private static string? EnsureExifTool()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string targetExePath = Path.Combine(baseDir, "exiftool.exe");
        string targetFolderPath = Path.Combine(baseDir, "exiftool_files");

        // 1. 如果本地已经解压好了，直接返回路径
        if (File.Exists(targetExePath) && System.IO.Directory.Exists(targetFolderPath))
        {
            return targetExePath;
        }

        // 2. 如果系统环境变量里有，直接使用系统里的
        if (IsInPath("exiftool.exe"))
            return "exiftool";

        // 3. 释放内嵌的 exiftool.zip 并解压
        try
        {
            string tempZipPath = Path.Combine(baseDir, "exiftool_temp.zip");

            // 提取 zip 资源
            ExtractResource("exiftool.zip", tempZipPath);

            // 解压到当前程序目录 (Overwrite = true)
            ZipFile.ExtractToDirectory(tempZipPath, baseDir, overwriteFiles: true);

            // 删除临时的 zip 包
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            if (File.Exists(targetExePath))
            {
                return targetExePath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 提取并解压 ExifTool 资源包失败: {ex.Message}");
        }

        return null;
    }

    private static string ExtractResource(string resourceFileName, string outputPath)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(resourceName))
        {
            throw new FileNotFoundException($"找不到内嵌资源: {resourceFileName}");
        }

        using (Stream? input = assembly.GetManifestResourceStream(resourceName))
        {
            if (input == null)
                throw new InvalidOperationException($"无法打开内嵌资源流: {resourceName}");
            using (Stream output = File.Create(outputPath))
            {
                input.CopyTo(output);
            }
        }
        return outputPath;
    }

    private static bool IsInPath(string fileName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        if (paths == null)
            return false;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(Path.Combine(path, fileName)))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static void PauseAndExit()
    {
        Console.WriteLine("\n按任意键退出程序...");
        Console.ReadKey(true);
    }

    private static void CreateDefaultConfig(string path)
    {
        ExtractResource("appsettings.json", path);
        Console.WriteLine("[提示] 自动从内嵌资源释放了默认的 appsettings.json 配置文件。");
    }
}

// ==========================================
// 5. C# 极其快速的 EXIF 解析核心
// ==========================================
public static class ExifHelper
{
    public static string GetExifValue(
        string filePath,
        string tagType,
        List<MetadataExtractor.Directory>? directories,
        out bool hasGPS
    )
    {
        hasGPS = false;
        if (directories == null)
            return "-";

        // 特殊判断：扫描是否有GPS定位（不管当前展示列中是否有GPS）
        var gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (
            gpsDir != null
            && (
                gpsDir.ContainsTag(GpsDirectory.TagLatitude)
                || gpsDir.ContainsTag(GpsDirectory.TagLongitude)
            )
        )
        {
            hasGPS = true;
        }

        try
        {
            switch (tagType.ToLower())
            {
                case "filename":
                    return Path.GetFileName(filePath);

                case "gps":
                    return hasGPS ? "有GPS" : "无";

                case "datetimeoriginal":
                    var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                    if (
                        subIfd != null
                        && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)
                    )
                        return dt.ToString("yyyy:MM:dd HH:mm:ss");
                    var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    if (
                        ifd0 != null
                        && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt2)
                    )
                        return dt2.ToString("yyyy:MM:dd HH:mm:ss");
                    return "-";

                case "camera":
                    var ifd0Cam = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    if (ifd0Cam != null)
                    {
                        string? make = ifd0Cam.GetString(ExifDirectoryBase.TagMake)?.Trim();
                        string? model = ifd0Cam.GetString(ExifDirectoryBase.TagModel)?.Trim();
                        if (!string.IsNullOrEmpty(make) || !string.IsNullOrEmpty(model))
                        {
                            return $"{make} {model}".Trim();
                        }
                    }
                    return "-";

                case "lens":
                    var subIfdLens = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                    return subIfdLens?.GetString(ExifDirectoryBase.TagLensModel) ?? "-";

                case "rating":
                    // Windows 星级
                    var ifd0Rating = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    if (ifd0Rating != null && ifd0Rating.TryGetInt32(0x4746, out var ratingVal))
                    {
                        if (ratingVal > 0 && ratingVal <= 5)
                            return $"{ratingVal} 星级";
                        if (ratingVal >= 99)
                            return "5 星级";
                        if (ratingVal >= 75)
                            return "4 星级";
                        if (ratingVal >= 50)
                            return "3 星级";
                        if (ratingVal >= 25)
                            return "2 星级";
                        if (ratingVal >= 1)
                            return "1 星级";
                    }
                    // Xmp 星级
                    var xmpDir = directories.OfType<XmpDirectory>().FirstOrDefault();
                    if (xmpDir != null && xmpDir.XmpMeta != null)
                    {
                        try
                        {
                            var xmpMeta = xmpDir.XmpMeta;
                            if (xmpMeta != null && xmpMeta.Properties != null)
                            {
                                // 在属性树中直接匹配 Rating 节点，彻底杜绝 XmpCore 内部的 Null 崩溃
                                var ratingProp = xmpMeta.Properties.FirstOrDefault(p =>
                                    p.Namespace == "http://ns.adobe.com/xap/1.0/"
                                    && p.Path != null
                                    && (
                                        p.Path == "Rating"
                                        || p.Path.EndsWith(
                                            "Rating",
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                    )
                                );

                                if (ratingProp != null && !string.IsNullOrEmpty(ratingProp.Value))
                                {
                                    if (int.TryParse(ratingProp.Value, out int rVal))
                                    {
                                        return $"{rVal} 星级";
                                    }
                                }
                            }
                        }
                        catch
                        { /* 确保不抛出异常破坏主程序执行 */
                        }
                    }
                    return "-";

                case "shutter":
                    var subIfdShutter = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                    if (
                        subIfdShutter != null
                        && subIfdShutter.TryGetRational(
                            ExifDirectoryBase.TagExposureTime,
                            out var rational
                        )
                    )
                    {
                        if (rational.Numerator == 0)
                            return "-";
                        if (rational.Numerator > rational.Denominator)
                        {
                            double seconds = (double)rational.Numerator / rational.Denominator;
                            return $"{seconds:0.#}s";
                        }
                        return $"{rational.Numerator}/{rational.Denominator}";
                    }
                    return "-";

                case "copyright":
                    var ifd0Copy = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    return ifd0Copy?.GetString(ExifDirectoryBase.TagCopyright) ?? "-";

                default:
                    return "-";
            }
        }
        catch
        {
            return "读取失败";
        }
    }
}

// ==========================================
// 6. 控制台 CJK 宽字符排版器
// ==========================================
public static class StringFormatter
{
    public static string GetFormattedString(string text, int width)
    {
        if (string.IsNullOrWhiteSpace(text))
            text = "-";

        int displayLength = 0;
        var result = new System.Text.StringBuilder();

        foreach (char c in text)
        {
            // CJK (中日韩) 字符的字符代码大于255，在控制台一般占 2 个字符宽度
            int charLen = ((int)c > 255) ? 2 : 1;

            if (displayLength + charLen > width - 2 && text.Length > result.Length + 1)
            {
                result.Append("..");
                displayLength += 2;
                break;
            }
            result.Append(c);
            displayLength += charLen;
        }

        int padCount = width - displayLength;
        if (padCount > 0)
        {
            result.Append(' ', padCount);
        }
        return result.ToString();
    }
}

// ==========================================
// 7. 配置映射实体
// ==========================================
public class AppConfig
{
    public ScanConfig Scan { get; set; } = new();
    public List<ColumnConfig> Columns { get; set; } = new();
}

public class ScanConfig
{
    public string FolderPath { get; set; } = "C:\\Photos";
    public string Filter { get; set; } = "*.*";
    public bool Recurse { get; set; } = false;
}

public class ColumnConfig
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Width { get; set; } = 15;
    public string ExifTag { get; set; } = "";
    public bool IsClearTarget { get; set; } = false;
    public List<string> ClearCommands { get; set; } = new();
}

public class TargetFile
{
    public string FilePath { get; set; } = "";
    public Dictionary<string, string> RowData { get; set; } = new();
    public List<string> ClearCommands { get; set; } = new();
}
