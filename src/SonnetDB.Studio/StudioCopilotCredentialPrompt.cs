using System.Diagnostics;
using System.Windows.Forms;

namespace SonnetDB.Studio;

/// <summary>在原生 STA 窗口输入短期公网 runtime token，敏感内容不返回 WebView。</summary>
internal static class StudioCopilotCredentialPrompt
{
    /// <summary>显示至多五分钟的原生凭据窗口，可被断开或请求取消关闭。</summary>
    public static Task<string?> ShowAsync(string publicOrigin, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var form = new Form
                {
                    Text = "SonnetDB Studio · 连接 AI 服务",
                    Width = 510,
                    Height = 230,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    StartPosition = FormStartPosition.CenterScreen,
                };
                var label = new Label
                {
                    Left = 18,
                    Top = 16,
                    Width = 465,
                    Height = 55,
                    Text = $"仅向 {publicOrigin} 发送。\n请输入已取得的短期公网访问令牌；不要输入数据库令牌。"
                };
                var password = new TextBox { Left = 18, Top = 78, Width = 465, UseSystemPasswordChar = true, MaxLength = 2048 };
                var connect = new Button { Text = "连接", Left = 305, Top = 126, Width = 84, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 399, Top = 126, Width = 84, DialogResult = DialogResult.Cancel };
                form.Controls.AddRange([label, password, connect, cancel]);
                form.AcceptButton = connect;
                form.CancelButton = cancel;
                using var timer = new System.Windows.Forms.Timer { Interval = 250 };
                var elapsed = Stopwatch.StartNew();
                int ticks = 0;
                timer.Tick += (_, _) =>
                {
                    if (++ticks >= 1200 || elapsed.Elapsed >= TimeSpan.FromMinutes(5) || cancellationToken.IsCancellationRequested)
                    { form.DialogResult = DialogResult.Cancel; form.Close(); }
                };
                form.Shown += (_, _) => { timer.Start(); password.Focus(); };
                string? value = form.ShowDialog() == DialogResult.OK && !cancellationToken.IsCancellationRequested ? password.Text : null;
                password.Clear();
                completion.TrySetResult(value);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        })
        { IsBackground = true, Name = "Studio Copilot credential prompt" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
