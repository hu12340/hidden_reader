using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HiddenReader.App.Data;

public sealed class BookInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
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
    /// <summary>-1 表示尚未记录位置（首次启动时放到工作区右侧）。</summary>
    public double WindowX { get; set; } = -1;
    public double WindowY { get; set; } = -1;
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
    }

    public string? GetDataDir() => RootDir;

    private void Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("library load failed: " + ex.Message);
            State = new AppState();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(State, JsonOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("library save failed: " + ex.Message);
        }
    }

    public BookInfo Import(string sourcePath)
    {
        string title = Path.GetFileNameWithoutExtension(sourcePath);
        string id = Guid.NewGuid().ToString("N");
        string fileName = id + ".txt";
        string destPath = Path.Combine(BooksDir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);

        var book = new BookInfo
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            FileName = fileName,
            SourcePath = sourcePath,
            AddedAt = DateTime.Now,
        };
        State.Books.Add(book);
        State.LastBookId = id;
        Save();
        Diagnostics.Log($"import '{book.Title}' from '{sourcePath}' -> '{destPath}'");
        return book;
    }

    /// <summary>移除书籍：只删除软件管理的副本，原文件不受影响。返回是否删除了副本。</summary>
    public bool Remove(BookInfo book)
    {
        string copyPath = Path.Combine(BooksDir, book.FileName);
        bool deleted = false;
        try
        {
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
                deleted = true;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("remove copy failed: " + ex.Message);
        }

        State.Books.RemoveAll(b => b.Id == book.Id);
        State.PerBook.Remove(book.Id);
        if (State.LastBookId == book.Id)
        {
            State.LastBookId = State.Books.Count > 0 ? State.Books[^1].Id : null;
        }
        Save();
        Diagnostics.Log($"remove '{book.Title}' (copy deleted={deleted})");
        return deleted;
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
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.GetEncoding("GB18030") })
        {
            try
            {
                using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (DecoderFallbackException)
            {
                // 尝试下一种编码
            }
        }
        return File.ReadAllText(path);
    }
}
