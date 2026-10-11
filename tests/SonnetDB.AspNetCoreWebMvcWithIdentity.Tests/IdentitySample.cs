namespace SonnetDB.AspNetCoreWebMvcWithIdentity.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SonnetDB.AspNetCoreWebMvcWithIdentity.Data;
using Xunit;

internal sealed class IdentitySample : IAsyncDisposable
{
    private const string DirectoryPrefix = "sndb-mvc-identity-";
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _directory;
    private SampleFactory _factory;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private HttpClient? _client;
    private bool _ownsDirectory;
    private bool _ownershipMarkerWritten;
    private int _requestCount;

    private IdentitySample()
    {
        _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DirectoryPrefix + _owner));
        _factory = new SampleFactory(_directory);
    }

    internal HttpClient Client => _client ?? throw new InvalidOperationException("测试宿主尚未启动。");

    internal CancellationToken Token => _timeout.Token;

    internal RecordingEmailSender Emails => _factory.Emails;

    internal static async Task<IdentitySample> CreateAsync(bool applyMigrations = true)
    {
        var sample = new IdentitySample();
        try
        {
            if (Directory.Exists(sample._directory))
            {
                throw new InvalidOperationException("测试目录已存在，拒绝复用其内容。");
            }

            Directory.CreateDirectory(sample._directory);
            sample._ownsDirectory = true;
            File.WriteAllText(Path.Combine(sample._directory, "test-owner"), sample._owner);
            sample._ownershipMarkerWritten = true;
            sample._client = sample._factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = true
            });
            sample.Client.Timeout = TimeSpan.FromSeconds(30);
            await using var scope = sample.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(sample._directory, Path.GetFullPath(context.Database.GetDbConnection().DataSource));
            if (applyMigrations)
            {
                await context.Database.MigrateAsync(sample.Token);
            }
            return sample;
        }
        catch
        {
            await sample.DisposeAsync();
            throw;
        }
    }

    internal AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    internal async Task RestartAsync()
    {
        Token.ThrowIfCancellationRequested();
        Client.Dispose();
        _client = null;
        await _factory.DisposeAsync();
        _factory = new SampleFactory(_directory);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        Client.Timeout = TimeSpan.FromSeconds(30);
        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(_directory, Path.GetFullPath(context.Database.GetDbConnection().DataSource));
    }

    internal async Task<HttpResponseMessage> GetAsync(string path)
    {
        CheckRequestBudget();
        return await Client.GetAsync(path, Token);
    }

    internal async Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> fields)
    {
        using var page = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Token);
        fields.Add("__RequestVerificationToken", ReadInput(html, "__RequestVerificationToken"));
        if (html.Contains("name=\"Input.Code\"", StringComparison.Ordinal))
        {
            fields.TryAdd("Input.Code", ReadInput(html, "Input.Code"));
        }

        CheckRequestBudget();
        using var content = new FormUrlEncodedContent(fields);
        return await Client.PostAsync(path, content, Token);
    }

    internal async Task<HttpResponseMessage> PostFromPageAsync(string pagePath, string actionPath, Dictionary<string, string> fields)
    {
        using var page = await GetAsync(pagePath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Token);
        fields.Add("__RequestVerificationToken", ReadInput(html, "__RequestVerificationToken"));
        CheckRequestBudget();
        using var content = new FormUrlEncodedContent(fields);
        return await Client.PostAsync(actionPath, content, Token);
    }

    internal async Task RegisterAndConfirmAsync(string email, string password)
    {
        using var registered = await PostAsync("/Identity/Account/Register", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password
        });
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        Assert.Contains("RegisterConfirmation", registered.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var confirmed = await GetAsync(Emails.LastLink(email));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Contains("Thank you for confirming your email", await confirmed.Content.ReadAsStringAsync(Token), StringComparison.OrdinalIgnoreCase);
    }

    internal Task<HttpResponseMessage> LoginAsync(string email, string password) => PostAsync("/Identity/Account/Login", new Dictionary<string, string>
    {
        ["Input.Email"] = email,
        ["Input.Password"] = password,
        ["Input.RememberMe"] = "true"
    });

    /// <summary>释放测试宿主并仅删除带有本次所有权标记的临时数据库。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _client?.Dispose();
            await _factory.DisposeAsync();
        }
        finally
        {
            _timeout.Dispose();
            if (_ownsDirectory)
            {
                var absolute = Path.GetFullPath(_directory);
                var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                var marker = Path.Combine(absolute, "test-owner");
                if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(absolute), DirectoryPrefix + _owner, StringComparison.Ordinal)
                    || (_ownershipMarkerWritten && (!File.Exists(marker) || File.ReadAllText(marker) != _owner)))
                {
                    throw new InvalidOperationException("拒绝删除所有权或绝对路径不匹配的测试目录。");
                }

                // 若所有权标记写入失败，只允许删除仍为空的、本次新建的目录。
                Directory.Delete(absolute, recursive: _ownershipMarkerWritten);
                _ownsDirectory = false;
            }
        }
    }

    private void CheckRequestBudget()
    {
        Token.ThrowIfCancellationRequested();
        if (++_requestCount > 80)
        {
            throw new InvalidOperationException("测试超过最多 80 次 HTTP 请求的预算。");
        }
    }

    private static string ReadInput(string html, string name)
    {
        var match = Regex.Match(html, "name=\"" + Regex.Escape(name) + "\"[^>]*value=\"([^\"]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, $"页面未提供表单字段 {name}。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed class SampleFactory(string directory) : WebApplicationFactory<Program>
    {
        internal RecordingEmailSender Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={directory}"
            }));
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "data-protection")));
                // Program 在延迟宿主配置回调前已捕获连接串，因此在原有 EF 注册后覆盖测试目录。
                services.ConfigureDbContext<ApplicationDbContext>(options => options.UseSonnetDB($"Data Source={directory}"));
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
                // 仅缩短测试的 cookie 校验间隔；保留样例要求确认账户的登录合同。
                services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
            });
        }
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<(string Email, string Message)> _messages = new();

    /// <summary>仅在测试内记录邮件内容，不连接或调用外部邮件服务。</summary>
    /// <param name="email">测试收件人。</param>
    /// <param name="subject">邮件主题。</param>
    /// <param name="htmlMessage">包含账户验证链接的正文。</param>
    /// <returns>已完成的记录任务。</returns>
    public Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        _messages.Enqueue((email, htmlMessage));
        return Task.CompletedTask;
    }

    internal string LastLink(string email)
    {
        var message = _messages.Last(item => item.Email == email).Message;
        var match = Regex.Match(message, "href=['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, "邮件未包含可使用的验证链接。");
        return new Uri(WebUtility.HtmlDecode(match.Groups[1].Value)).PathAndQuery;
    }
}
