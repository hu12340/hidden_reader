using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HiddenReader.App.Data;

public sealed class BookInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>书架下拉框等直接以标题显示（自定义模板下 DisplayMemberPath 不生效）。</summary>
    public override string ToString() => Title;
    /// <summary>软件管理的副本文件名（位于 books 目录下）。</summary>
    public string FileName { get; set; } = "";
    /// <summary>导入时的原文件路径（仅供展示；副本打开不依赖它）。</summary>
    public string SourcePath { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

public sealed class FloatState
{
    public double BackgroundOpacity { get; set; } = 0.85;
    public double BorderOpacity { get; set; } = 1.0;
    public bool Topmost { get; set; } = true;
    /// <summary>-1 表示尚未放置，首次进入浮窗时放到工作区右下角。</summary>
    public double X { get; set; } = -1;
    public double Y { get; set; } = -1;
    public double Width { get; set; } = 460;
    public double Height { get; set; } = 360;
}

public sealed class BookState
{
    /// <summary>当前页首的正文偏移量。</summary>
    public int Anchor { get; set; }
    public FloatState Float { get; set; } = new();
}

public sealed class AppState
{
    public List<BookInfo> Books { get; set; } = new();
    public string? LastBookId { get; set; }
    /// <summary>"normal" 或 "float"：上次退出时所在的模式。</summary>
    public string LastMode { get; set; } = "normal";
    public Dictionary<string, BookState> PerBook { get; set; } = new();
    /// <summary>null 表示尚未记录位置（首次启动时放到工作区右侧）；
    /// 有效坐标可以为负（左侧/上方显示器）。旧版文件中的 -1/-1 兼容判定见 WindowPlacement。</summary>
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 640;
}

/// <summary>
/// 离线书架：书籍以副本形式存放在 %APPDATA%\HiddenReader\books，
/// 元数据存放在 library.json。移除只删除副本，不动原文件。
/// </summary>
public sealed class Library
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string RootDir { get; }
    public string BooksDir => Path.Combine(RootDir, "books");
    private string StatePath => Path.Combine(RootDir, "library.json");

    public AppState State { get; private set; } = new();

    public Library()
    {
        RootDir = Path.Combine(
            Environment.GetEnvironmentVariable("HR_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HiddenReader"));
        Directory.CreateDirectory(BooksDir);
        Load();
        CleanupOrphans();
    }

    public string? GetDataDir() => RootDir;

    private readonly object _saveLock = new();
    /// <summary>状态文件损坏时置位：保留磁盘上的原文件与书籍副本，禁止用空书架覆盖。</summary>
    private bool _stateInvalid;
    /// <summary>仅当成功读取有效书架时为真；状态文件缺失或损坏时为假，启动清理不得运行。</summary>
    private bool _shelfLoaded;

    /// <summary>导入命名规则：Guid "N" 格式（32 位十六进制）+.txt；临时文件再加 .tmp 后缀。</summary>
    private static readonly Regex AppCopyNameRegex =
        new("^[0-9a-f]{32}\\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppTempNameRegex =
        new("^[0-9a-f]{32}\\.txt\\.tmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>保留清单：状态丢失期间发现的应用命名副本，防止书架恢复后被当孤儿误删。</summary>
    private string PreservedListPath => Path.Combine(RootDir, "library.preserved.json");

    private void Load()
    {
        if (!File.Exists(StatePath))
        {
            // 状态缺失：书架引用关系不可知，先记录现有应用副本，绝不清理。
            PreserveExistingCopies();
            return;
        }
        try
        {
            string json = File.ReadAllText(StatePath);
            // 只有显式包含非 null、可读取的 Books 数组才认作有效书架。
            // AppState.Books 有属性初始化器，缺失字段反序列化后是空列表而非 null，
            // 仅检查反序列化结果会把不完整/被截断的状态误当空书架，进而清理副本。
            // 显式 "Books": [] 仍是有效空书架。
            using var doc = JsonDocument.Parse(json);
            bool validShelf = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Books", out var booksElement)
                && booksElement.ValueKind == JsonValueKind.Array;
            if (!validShelf)
            {
                throw new InvalidOperationException("state file has no readable Books array");
            }
            State = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
                    ?? throw new InvalidOperationException("state file is empty");
            _shelfLoaded = true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("library load failed (state preserved): " + ex.Message);
            State = new AppState();
            _stateInvalid = true;
            PreserveExistingCopies();
        }
    }

    /// <summary>保留清单读取结果：区分「确实没有清单」「有效清单（含 []）」与「存在但不可信」。</summary>
    private enum PreservedListStatus { Missing, Valid, Invalid }

    /// <summary>
    /// 读取保留清单。用 File.OpenRead 区分「确实不存在」（FileNotFoundException/
    /// DirectoryNotFoundException）与「存在但读取失败」（权限、被占用等其他 I/O 错误）——
    /// File.Exists 在权限不足时同样返回 false，会把后者误判为不存在。
    /// JSON 损坏、内容为 null、元素非字符串等结构错误一律判为 Invalid。
    /// </summary>
    private PreservedListStatus TryLoadPreservedList(out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = File.OpenRead(PreservedListPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Diagnostics.Log("preserved list unusable: root is not an array");
                return PreservedListStatus.Invalid;
            }
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    Diagnostics.Log("preserved list unusable: non-string element");
                    return PreservedListStatus.Invalid;
                }
                names.Add(element.GetString()!);
            }
            return PreservedListStatus.Valid;
        }
        catch (FileNotFoundException) { return PreservedListStatus.Missing; }
        catch (DirectoryNotFoundException) { return PreservedListStatus.Missing; }
        catch (Exception ex)
        {
            Diagnostics.Log("preserved list unusable: " + ex.Message);
            return PreservedListStatus.Invalid;
        }
    }

    /// <summary>
    /// 状态缺失或无效时调用：把书库中应用命名的副本记入保留清单（library.preserved.json）。
    /// 之后即使书架被重建且不引用这些副本，启动清理也会跳过它们，避免误删用户仅存的副本。
    /// 外来文件无需记录——清理白名单本就不会触碰它们。
    /// 清单存在但不可信时不写回：把失败当空集合写回会覆盖故障现场。
    /// </summary>
    private void PreserveExistingCopies()
    {
        var status = TryLoadPreservedList(out var preserved);
        if (status == PreservedListStatus.Invalid)
        {
            Diagnostics.Log("preserve skipped: preserved list unusable, fault site kept");
            return;
        }
        try
        {
            int added = 0;
            foreach (string file in Directory.EnumerateFiles(BooksDir))
            {
                string name = Path.GetFileName(file);
                if (!AppCopyNameRegex.IsMatch(name)) continue;
                if (preserved.Add(name)) added++;
            }
            if (added > 0)
            {
                WritePreservedList(preserved);
                Diagnostics.Log($"preserved {added} existing copy(ies): state missing/invalid");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("preserve existing copies failed: " + ex.Message);
        }
    }

    private void WritePreservedList(HashSet<string> names)
    {
        File.WriteAllText(PreservedListPath,
            JsonSerializer.Serialize(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions));
    }

    /// <summary>
    /// 原子保存状态：先写同目录临时文件，再替换正式文件（首次保存时目标不存在则直接改名）。
    /// 返回是否提交成功；失败时内存状态保持不变，由调用方决定回滚。
    /// </summary>
    public bool Save()
    {
        if (_stateInvalid)
        {
            Diagnostics.Log("library save skipped: state file invalid, original file preserved");
            return false;
        }
        lock (_saveLock)
        {
            string tmpPath = StatePath + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(State, JsonOptions));
                if (File.Exists(StatePath))
                {
                    File.Replace(tmpPath, StatePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, StatePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("library save failed: " + ex.Message);
                try { File.Delete(tmpPath); } catch { /* 清理失败不影响结果 */ }
                return false;
            }
        }
    }

    /// <summary>
    /// 启动清理：仅在成功读取有效书架（_shelfLoaded）后调用，且只按文件名白名单处理应用资产：
    /// 32 位十六进制 ID 命名的 .txt 副本（仅删除「无引用且不在保留清单中」的孤儿）和
    /// &lt;id&gt;.txt.tmp 导入临时文件。外来文件（keepme.txt、其他 .tmp/.dat 等）一律保留。
    /// 状态文件缺失或损坏时不进入本方法，副本全部保留（见 PreserveExistingCopies）。
    /// 保留清单存在但不可信时同样跳过整次清理：无法区分「受保护副本」与「孤儿」，
    /// 宁可少删不可误删（原因已记录到日志）。
    /// </summary>
    public void CleanupOrphans()
    {
        if (!_shelfLoaded) return;
        var status = TryLoadPreservedList(out var preserved);
        if (status == PreservedListStatus.Invalid)
        {
            Diagnostics.Log("startup cleanup skipped: preserved list unusable");
            return;
        }
        try
        {
            var referenced = new HashSet<string>(
                State.Books.Select(b => b.FileName),
                StringComparer.OrdinalIgnoreCase);
            bool preservedChanged = false;
            foreach (string file in Directory.EnumerateFiles(BooksDir))
            {
                string name = Path.GetFileName(file);
                if (referenced.Contains(name))
                {
                    // 已被书架引用：受保护，同时退出保留清单（此后引用关系即可保护它）。
                    if (preserved.Remove(name)) preservedChanged = true;
                    continue;
                }
                bool isImportTemp = AppTempNameRegex.IsMatch(name);
                bool isOrphanCopy = AppCopyNameRegex.IsMatch(name) && !preserved.Contains(name);
                if (!isImportTemp && !isOrphanCopy) continue;
                try
                {
                    File.Delete(file);
                    Diagnostics.Log("startup cleanup removed '" + name + "'");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("startup cleanup failed for '" + name + "': " + ex.Message);
                }
            }
            if (preservedChanged) WritePreservedList(preserved);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("startup cleanup failed: " + ex.Message);
        }
    }

    public BookInfo Import(string sourcePath)
    {
        string title = Path.GetFileNameWithoutExtension(sourcePath);
        string id = Guid.NewGuid().ToString("N");
        string fileName = id + ".txt";
        string destPath = Path.Combine(BooksDir, fileName);

        // 先写临时文件，完整落盘后改名为正式副本；失败时正式路径不受影响。
        string tmpPath = destPath + ".tmp";
        File.Copy(sourcePath, tmpPath, overwrite: true);
        File.Move(tmpPath, destPath, overwrite: true);

        var book = new BookInfo
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            FileName = fileName,
            SourcePath = sourcePath,
            AddedAt = DateTime.Now,
        };
        string? previousLastBookId = State.LastBookId;
        State.Books.Add(book);
        State.LastBookId = id;
        if (!Save())
        {
            // 书架条目提交失败：回滚内存状态并删除副本，界面按失败提示。
            State.Books.Remove(book);
            State.LastBookId = previousLastBookId;
            try { File.Delete(destPath); } catch { /* 回滚删除失败则留给启动清理 */ }
            throw new InvalidOperationException("书架状态保存失败，导入已回滚。");
        }
        Diagnostics.Log($"import '{book.Title}' from '{sourcePath}' -> '{destPath}'");
        return book;
    }

    /// <summary>移除书籍：先提交书架变更，成功后再删除软件管理的副本；原文件不受影响。</summary>
    public bool Remove(BookInfo book)
    {
        int index = State.Books.IndexOf(book);
        BookState? perBook = State.PerBook.TryGetValue(book.Id, out var bs) ? bs : null;
        string? previousLastBookId = State.LastBookId;

        State.Books.Remove(book);
        State.PerBook.Remove(book.Id);
        if (State.LastBookId == book.Id)
        {
            State.LastBookId = State.Books.Count > 0 ? State.Books[^1].Id : null;
        }
        if (!Save())
        {
            // 提交失败：恢复内存状态，副本保留，调用方按失败处理。
            State.Books.Insert(index, book);
            if (perBook is not null) State.PerBook[book.Id] = perBook;
            State.LastBookId = previousLastBookId;
            Diagnostics.Log("remove rolled back: state commit failed");
            return false;
        }

        string copyPath = Path.Combine(BooksDir, book.FileName);
        try
        {
            if (File.Exists(copyPath)) File.Delete(copyPath);
        }
        catch (Exception ex)
        {
            // 书架已提交，副本删除失败不影响移除结果；副本成为无引用文件，由启动清理兜底。
            Diagnostics.Log("remove copy failed: " + ex.Message);
        }
        Diagnostics.Log($"remove '{book.Title}'");
        return true;
    }

    public BookState GetBookState(string bookId)
    {
        if (!State.PerBook.TryGetValue(bookId, out var state))
        {
            state = new BookState();
            State.PerBook[bookId] = state;
        }
        return state;
    }

    /// <summary>读取书籍副本正文；副本存在即可打开，与原文件是否改名无关。</summary>
    public string ReadBook(BookInfo book)
    {
        string path = Path.Combine(BooksDir, book.FileName);
        byte[] bytes = File.ReadAllBytes(path);

        // BOM 优先：与原实现一致，带 BOM 的文件按 BOM 解码。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // 无 BOM：先按 UTF-8 严格解码；遇到无效字节（典型的 GB18030 内容）再回退。
        // 不能依赖 StreamReader 的默认替换回退——它会把无效字节静默替换成 U+FFFD，
        // 导致 GB18030 内容永远得不到尝试。
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}
