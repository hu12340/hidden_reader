using HiddenReader.Core.Reading;

namespace HiddenReader.App.UI;

/// <summary>
/// 全书分页的后台线程。Paginator.Layout 依赖 WPF 文本测量（FormattedText），
/// 不能在 MTA 线程运行、也不应阻塞 UI 线程，因此使用专用 STA 线程逐页计算，
/// 期间普通窗口保持可重绘、可移动、可翻页。
/// 同一时刻只保留最新请求：切书或窗口缩放后，未完成的旧计算被放弃。
/// </summary>
internal sealed class BackgroundPager : IDisposable
{
    public sealed record PagerRequest(ReadingSession Session, double Width, double Height, double Ppd);
    public sealed record Result(ReadingSession Session, List<int> Starts, double Width, double Height, double Ppd);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _thread;
    private readonly Action<Result> _onCompleted;
    private PagerRequest? _pending;
    private bool _disposed;

    public BackgroundPager(Action<Result> onCompleted)
    {
        _onCompleted = onCompleted;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HiddenReader.Pager" };
        // FormattedText 测量要求 STA；结果回调由调用方封送回 UI 线程。
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>请求对指定会话与布局尺寸做全书分页；重复请求只保留最新一次。</summary>
    public void Request(ReadingSession session, double width, double height, double ppd)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = new PagerRequest(session, width, height, ppd);
            _signal.Release();
        }
    }

    private void Loop()
    {
        while (true)
        {
            _signal.Wait();
            PagerRequest? request;
            lock (_gate)
            {
                if (_disposed) return;
                request = _pending;
                _pending = null;
            }
            if (request is null) continue;
            var result = Compute(request);
            if (result is not null) _onCompleted(result);
        }
    }

    private Result? Compute(PagerRequest request)
    {
        string text = request.Session.Text;
        var starts = new List<int> { 0 };
        int start = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastPendingCheck = 0;
        while (start < text.Length)
        {
            // 计算中途出现新请求（切书、缩放）时放弃本次结果，避免排队滞后。
            if (stopwatch.ElapsedMilliseconds - lastPendingCheck >= 16)
            {
                lastPendingCheck = stopwatch.ElapsedMilliseconds;
                lock (_gate)
                {
                    if (_disposed || _pending is not null) return null;
                }
            }
            var page = Paginator.Layout(text, start, request.Width, request.Height, request.Session.Settings, request.Ppd);
            if (page.End <= start) break;
            start = page.End;
            if (start >= text.Length) break;
            // 新页首之后只剩空白时不再新增页：否则按页跳转会落到空白页
            // （与 ReadingSession.ComputePageStarts、ScrollDownLines 的到底判定同一语义）。
            if (text.AsSpan(start).IsWhiteSpace()) break;
            starts.Add(start);
        }
        return new Result(request.Session, starts, request.Width, request.Height, request.Ppd);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        _signal.Release();
    }
}
