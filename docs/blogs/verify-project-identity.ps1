param([string[]]$ArticleId)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw '必须使用 PowerShell 7。'
}

$deadline = [DateTimeOffset]::Now.AddSeconds(30)
$blogRoot = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $blogRoot '../..'))
$state = Get-Content -Raw (Join-Path $blogRoot 'publishing-state.json') | ConvertFrom-Json -DateKind String
if ($ArticleId) {
    if ($ArticleId.Count -gt 10) {
        throw '一次最多核查 10 篇文章。'
    }
    $articles = @($state.articles | Where-Object { $_.articleId -in $ArticleId })
    if ($articles.Count -ne @($ArticleId | Select-Object -Unique).Count) {
        throw '指定文章编号不存在。'
    }
}
else {
    $articles = @($state.articles | Where-Object status -eq 'queued' | Sort-Object scheduledAt, priority, articleId | Select-Object -First 10)
}

foreach ($article in $articles) {
    if ([DateTimeOffset]::Now -ge $deadline) {
        throw '项目识别检查超过 30 秒。'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $article.path))
    if (-not $path.StartsWith($blogRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "文章路径不在博客目录中：$($article.articleId)"
    }
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $document = [regex]::Match($text, '\A---\n(?<front>.*?)\n---\n(?<body>.*)\z', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $document.Success) {
        throw "缺少完整 front matter：$($article.articleId)"
    }
    $title = [regex]::Match($document.Groups['front'].Value, '(?m)^title:\s*(.+)$').Groups[1].Value.Trim().Trim('"', "'")
    $body = $document.Groups['body'].Value.TrimStart()
    $heading = [regex]::Match($body, '\A# ([^\n]+)').Groups[1].Value.Trim()
    $withoutHeading = [regex]::Replace($body, '\A# [^\n]+\n+', '')
    $opening = [regex]::Match($withoutHeading, '\A(?<paragraph>.*?)(?:\n\s*\n|$)', [Text.RegularExpressions.RegexOptions]::Singleline).Groups['paragraph'].Value
    if ($article.title -cnotmatch '^SonnetDB\b' -or $title -cnotmatch '^SonnetDB\b' -or $heading -cnotmatch '^SonnetDB\b') {
        throw "发布标题、front matter 和正文 H1 必须以 SonnetDB 开头：$($article.articleId)"
    }
    if ($article.title -cne $title -or $title -cne $heading) {
        throw "发布标题、front matter 与正文 H1 不一致：$($article.articleId)"
    }
    if (-not $opening.Contains('SonnetDB', [StringComparison]::Ordinal)) {
        throw "正文首段必须直接说明 SonnetDB：$($article.articleId)"
    }
    $expectedContentSha256 = $article.contentSha256
    if ($article.status -eq 'published' -and $article.contentRevision) {
        $expectedContentSha256 = $article.contentRevision.contentSha256
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedContentSha256) {
        throw "正文 SHA256 与状态不一致：$($article.articleId)"
    }
    Write-Output "项目识别检查通过：$($article.articleId) $title"
}

if ($articles.Count -eq 0) {
    Write-Output '当前没有待检查的 queued 文章。'
}
