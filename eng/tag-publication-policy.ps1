Set-StrictMode -Version Latest

function Assert-TaggedPublicationSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Run,
        [Parameter(Mandatory)][ValidateCount(1, 100)][object[]]$Jobs,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)]
        [ValidatePattern('\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')]
        [string]$Tag,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$CommitSha,
        [Parameter(Mandatory)][ValidateSet('publish.yml', 'connectors-release.yml')][string]$Workflow
    )

    if ($Run.repository.full_name -cne $Repository -or $Run.event -cne 'push' -or
        $Run.path -cne ".github/workflows/$Workflow" -or $Run.head_branch -cne $Tag -or
        $Run.head_sha -cne $CommitSha -or $Run.status -cne 'completed') {
        throw "Source $Workflow run is not a completed tagged run of the frozen repository and commit."
    }

    # 发布本身允许因凭据失败；构建及真实安装/协议合同必须已完整成功。
    $requiredJobs = if ($Workflow -ceq 'publish.yml') {
        @('NuGet Packages', 'Release Bundles (linux-x64)', 'Release Bundles (win-x64)')
    }
    else {
        @('Connectors (win-x64)', 'Connectors (linux-x64)', 'Connectors (win-x86)')
    }
    foreach ($name in $requiredJobs) {
        $matches = @($Jobs | Where-Object name -CEQ $name)
        if ($matches.Count -ne 1 -or $matches[0].status -cne 'completed' -or
            $matches[0].conclusion -cne 'success') {
            throw "Source $Workflow has no unique successful contract job: $name"
        }
    }
}
