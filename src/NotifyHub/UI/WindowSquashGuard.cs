using System;
using System.Windows;
using System.Windows.Threading;

namespace NotifyHub.UI;

/// <summary>
/// v0.2.16：分层窗口「最小化被压扁」补丁。
///
/// <para>
/// 现象：设置窗口点"最小化"（或按 Win+D）之后，屏幕**左下角**留下一块白色小方块，
/// 关掉 NotifyHub 才消失。实测该窗口停在 <c>0,1012 / 160×28 / showCmd=2</c>。
/// </para>
/// <para>
/// 根因：<c>WindowStyle=None + AllowsTransparency=True</c> 的分层窗口没有系统标题栏，
/// WPF 不会走 DWM 的最小化动画，而是直接把 HWND 缩成 160×28 左右、挪到主屏左下角，
/// **并且继续保持可见、继续绘制**。于是那块白色圆角就是窗口本体，不是"残影"。
/// </para>
/// <para>
/// 处理：监听 <see cref="Window.StateChanged"/>，一旦变成 Minimized 就立刻拉回 Normal
/// 并 <see cref="UIElement.Hide"/>，即"最小化 = 收进托盘"。这类窗口本来
/// <c>ShowInTaskbar=False</c>，没有任务栏按钮要照顾；用户通过托盘双击或
/// <c>--open-settings</c> 就能叫回来（App.ShowSettings 会 Show + Activate）。
/// </para>
/// </summary>
public static class WindowSquashGuard
{
    /// <summary>WPF 压扁后的典型尺寸（判定自检用）。</summary>
    public const double SquashedWidth = 160;

    /// <summary>
    /// 给窗口挂上补丁。<paramref name="onSquashed"/> 只在真的拦截到一次最小化时回调一次，
    /// 供调用方写日志（默认不写，避免 Routine 日志刷屏）。
    /// </summary>
    public static void Attach(Window window, Action<string>? onSquashed = null)
    {
        if (window is null)
        {
            return;
        }

        // 记住"正常态"的位置与尺寸：
        // 有些机器上 WPF 在派发 StateChanged 之前就已经把窗口缩成 160×28 了，
        // 那时 RestoreBounds 已经不可信，只能靠我们自己记账。
        Rect? normalRect = null;

        void Remember()
        {
            if (window.WindowState == WindowState.Normal
                && window.IsVisible
                && window.ActualWidth > 1
                && window.ActualHeight > 1)
            {
                normalRect = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            }
        }

        window.LocationChanged += (_, _) => Remember();
        window.SizeChanged += (_, _) => Remember();

        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized)
            {
                return;
            }

            // 用 Send 优先级排队：状态变更事件里直接改 WindowState 会触发重入
            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (window.WindowState != WindowState.Minimized)
                    {
                        return;
                    }

                    window.WindowState = WindowState.Normal;

                    if (normalRect is { } rect && rect.Width > 1 && rect.Height > 1)
                    {
                        window.Left = rect.X;
                        window.Top = rect.Y;
                        window.Width = rect.Width;
                        window.Height = rect.Height;
                    }

                    window.Hide();
                    onSquashed?.Invoke("最小化已转成「收进托盘」，不留左下角白块");
                }),
                DispatcherPriority.Send);
        };
    }

    /// <summary>
    /// 判定窗口是否正处在"被压扁"的坏状态（自检/诊断用）：
    /// 最小化 + 尺寸塌成 160×28 那一类，或者最小化却仍然可见。
    /// </summary>
    public static bool IsSquashed(Window window)
    {
        if (window is null || window.WindowState != WindowState.Minimized)
        {
            return false;
        }

        // 最小化后还"可见"、或者宽高塌到 200×60 以内的，都是会被画出来的那种坏状态
        return window.IsVisible
               || (window.ActualWidth > 0 && window.ActualWidth <= 200 && window.ActualHeight <= 60);
    }
}
