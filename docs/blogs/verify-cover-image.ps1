#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]]$ArticleId,
    [switch]$RequireUploadReady,
    [string]$ConfigPath = 'C:\Users\mysti\.codex\skills\publish-cnblogs\config\settings.json'
)

$ErrorActionPreference = 'Stop'
$deadline = [DateTimeOffset]::Now.AddSeconds(30)
$blogRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $blogRoot '../..'))
$state = Get-Content -Raw (Join-Path $blogRoot 'publishing-state.json') | ConvertFrom-Json -DateKind String
if ($ArticleId) {
    if ($ArticleId.Count -gt 10) { throw '一次最多核查 10 篇。' }
    $articles = @($state.articles | Where-Object { $_.articleId -in $ArticleId })
    if ($articles.Count -ne @($ArticleId | Select-Object -Unique).Count) { throw '文章编号不存在。' }
}
else {
    $articles = @($state.articles | Where-Object status -eq 'queued' | Sort-Object scheduledAt, priority, articleId | Select-Object -First 10)
}

foreach ($article in $articles) {
    if ([DateTimeOffset]::Now -ge $deadline) { throw '配图检查超过 30 秒。' }
    $cover = $article.coverImage
    if (-not $cover -or $cover.reviewStatus -cne 'approved' -or -not $cover.reviewedAt) {
        throw "缺少已目视审核的主题封面：$($article.articleId)"
    }
    $sourceEvidence = $cover.generationReceipt
    if (-not $sourceEvidence) { $sourceEvidence = $cover.sourceEvidence }
    if (-not $cover.sourceThreadId -or -not $cover.sourceAsset -or -not $sourceEvidence) {
        throw "封面缺少来源会话/资产/生成或复用证据：$($article.articleId)"
    }
    foreach ($field in @('alt', 'caption', 'model', 'requestedSize', 'actualSize', 'markdownUrl')) {
        if ([DateTimeOffset]::Now -ge $deadline) { throw '配图检查超过 30 秒。' }
        if ([string]::IsNullOrWhiteSpace([string]$cover.$field)) { throw "封面缺少 $field：$($article.articleId)" }
    }
    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $article.path))
    $imagePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $cover.path))
    $evidencePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $sourceEvidence))
    foreach ($candidate in @($path, $imagePath, $evidencePath)) {
        if ([DateTimeOffset]::Now -ge $deadline) { throw '配图检查超过 30 秒。' }
        if (-not $candidate.StartsWith($blogRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($candidate)) {
            throw "正文/封面不存在或越出博客目录：$($article.articleId)"
        }
    }
    $actualHash = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($cover.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $actualHash -cne $cover.sha256) { throw "封面 SHA256 不符：$($article.articleId)" }
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $reference = "![$($cover.alt)]($($cover.markdownUrl))"
    $bodyStart = [regex]::Match($text, '\A---\n.*?\n---\n\s*# [^\n]+\n+(?<opening>[^\n]+)\n', [Text.RegularExpressions.RegexOptions]::Singleline)
    $referenceOffset = $text.IndexOf($reference, [StringComparison]::Ordinal)
    $firstSection = $text.IndexOf("`n## ", [StringComparison]::Ordinal)
    if (-not $bodyStart.Success -or -not $bodyStart.Groups['opening'].Value.Contains('SonnetDB', [StringComparison]::Ordinal) -or $referenceOffset -lt $bodyStart.Index + $bodyStart.Length -or ($firstSection -ge 0 -and $referenceOffset -gt $firstSection) -or -not $text.Contains($cover.caption, [StringComparison]::Ordinal)) {
        throw "封面/图注必须位于首个 SonnetDB 叙述段之后、主体之前：$($article.articleId)"
    }
    $expectedBodyHash = $article.contentSha256
    if ($article.status -eq 'published' -and $article.contentRevision) { $expectedBodyHash = $article.contentRevision.contentSha256 }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedBodyHash) { throw "正文 SHA256 不符：$($article.articleId)" }
    $remoteUri = $null
    $isRemote = [uri]::TryCreate($cover.markdownUrl, [UriKind]::Absolute, [ref]$remoteUri) -and $remoteUri.Scheme -eq 'https'
    if (-not $isRemote) {
        $referencedPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $path -Parent) $cover.markdownUrl))
        if ($referencedPath -ine $imagePath) { throw "正文图片路径与封面资产不一致：$($article.articleId)" }
    }
    if ($RequireUploadReady) {
        if ($isRemote) {
            if ($cover.remoteUrl -cne $cover.markdownUrl -or -not $cover.remoteVerifiedAt -or $cover.remoteSha256 -cne $cover.sha256) { throw "远程封面缺少公开原图核验：$($article.articleId)" }
        }
        else {
            $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
            foreach ($field in @('blogId', 'metaWeblogUsername', 'metaWeblogPassword', 'metaWeblogEndpoint')) {
                if ([DateTimeOffset]::Now -ge $deadline) { throw '配图检查超过 30 秒。' }
                if ([string]::IsNullOrWhiteSpace([string]$config.$field)) { throw "本地封面上传缺少 $field；dry-run 不能证明上传：$($article.articleId)" }
            }
            $config = $null
        }
    }
    Write-Output "配图检查通过：$($article.articleId) $($cover.sha256)"
}
if ($articles.Count -eq 0) { Write-Output '当前没有待检查的 queued 文章。' }
