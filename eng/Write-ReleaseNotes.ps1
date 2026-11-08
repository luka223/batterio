[CmdletBinding()]
param(
    # Tag of the previous release; when it does not exist locally, the whole history is used.
    [Parameter(Mandatory)] [string]$PreviousTag,
    [Parameter(Mandatory)] [string]$Version,
    # owner/name, used for the compare link.
    [Parameter(Mandatory)] [string]$Repository,
    [string]$OutputPath = "release-notes.md",
    [string]$Head = "HEAD"
)

# Builds the release notes from first-parent history on main, so direct commits and
# pull requests (squash, merge commit or rebase) all end up as one line each:
#   - <commit subject> (<short sha>)
#   - <PR title> (#N)

$ErrorActionPreference = "Stop"

& git rev-parse -q --verify "refs/tags/$PreviousTag" *> $null
$hasPrevious = $LASTEXITCODE -eq 0
$global:LASTEXITCODE = 0
$range = if ($hasPrevious) { "$PreviousTag..$Head" } else { $Head }

# Fields: hash, parents, subject, body; records separated by 0x1e, fields by 0x1f.
$log = & git log --first-parent --reverse --format="%h%x1f%p%x1f%s%x1f%b%x1e" $range
if ($LASTEXITCODE -ne 0) {
    throw "git log failed with exit code $LASTEXITCODE."
}

$lines = [Collections.Generic.List[string]]::new()
foreach ($record in (($log -join "`n") -split [char]0x1e)) {
    $record = $record.Trim()
    if (-not $record) { continue }

    $hash, $parents, $subject, $body = $record -split [char]0x1f, 4
    $isMerge = @($parents -split ' ' | Where-Object { $_ }).Count -gt 1

    if ($isMerge) {
        if ($subject -match '^Merge pull request #(\d+) from ') {
            $number = $Matches[1]
            $title = @("$body" -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -First 1
            if (-not $title) { $title = $subject }
            $lines.Add("- $title (#$number)")
        }
        # Any other merge (e.g. "Merge branch 'main' of ..." from a local git pull) is not a change
        # of its own, but the commits it brought in from the other side are listed as direct commits.
        $sideRange = @("$hash^1..$hash^2")
        if ($hasPrevious) { $sideRange += "^$PreviousTag" }
        $side = & git log --no-merges --reverse --format="%h%x1f%s" @sideRange
        if ($LASTEXITCODE -ne 0) {
            throw "git log failed for merge $hash with exit code $LASTEXITCODE."
        }
        foreach ($entry in $side) {
            $sideHash, $sideSubject = $entry -split [char]0x1f, 2
            if ($sideSubject -match '\(#\d+\)\s*$') { $lines.Add("- $sideSubject") }
            else { $lines.Add("- $sideSubject ($sideHash)") }
        }
        continue
    }

    if ($subject -match '\(#\d+\)\s*$') {
        $lines.Add("- $subject")          # squash-merged PR: GitHub already appended (#N)
    } else {
        $lines.Add("- $subject ($hash)")  # direct commit
    }
}

$notes = [Text.StringBuilder]::new()
[void]$notes.AppendLine("## What's Changed")
[void]$notes.AppendLine()
if ($lines.Count -eq 0) {
    [void]$notes.AppendLine("- No changes recorded.")
} else {
    foreach ($line in $lines) { [void]$notes.AppendLine($line) }
}
[void]$notes.AppendLine()
if ($hasPrevious) {
    [void]$notes.AppendLine("**Full Changelog**: https://github.com/$Repository/compare/$PreviousTag...$Version")
} else {
    [void]$notes.AppendLine("**Full Changelog**: https://github.com/$Repository/commits/$Version")
}

[IO.File]::WriteAllText($OutputPath, $notes.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host $notes.ToString()
