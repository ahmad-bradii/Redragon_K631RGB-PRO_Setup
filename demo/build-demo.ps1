# Builds k631-live-demo.html: the current K631 Live page (web\index.html) running against the simulated
# keyboard in demo-shim.js, with the guided tour from demo-tour.html. Re-run after changing the app.
$ErrorActionPreference = 'Stop'
$enc = New-Object Text.UTF8Encoding($false)
$app = [IO.File]::ReadAllText("$PSScriptRoot\..\web\index.html", $enc)
$shim = [IO.File]::ReadAllText("$PSScriptRoot\demo-shim.js", $enc)
$tour = [IO.File]::ReadAllText("$PSScriptRoot\demo-tour.html", $enc)

function Between($text, $start, $end) {
    $i = $text.IndexOf($start)
    $j = $text.IndexOf($end, $i + $start.Length)
    if ($i -lt 0 -or $j -lt 0) { throw "Could not find $start ... $end in web\index.html" }
    $text.Substring($i + $start.Length, $j - $i - $start.Length)
}

$fonts = [regex]::Matches($app, '<link [^>]*>') | ForEach-Object { $_.Value }
$style = Between $app '<style>' '</style>'
$body = Between $app '<body>' '</body>'
$scriptAt = $body.IndexOf('<script>')
if ($scriptAt -lt 0) { throw 'No <script> in web\index.html' }

# Page-contract adjustments for a published page: side gutters that clear a phone's system bars,
# and a pinned header that sits below the top safe area instead of padding for it.
$contract = @'
.wrap{padding-left:max(16px, env(safe-area-inset-left, 0px)); padding-right:max(16px, env(safe-area-inset-right, 0px));}
.topbar{padding-top:10px; padding-left:max(20px, env(safe-area-inset-left, 0px)); padding-right:max(20px, env(safe-area-inset-right, 0px));}
.dock{top:env(safe-area-inset-top, 0px);}
'@

$page = "<title>K631 Live Demo</title>`n" +
        "<meta name=`"claude:viewport-fit-sides`" content=`"cover`">`n" +
        ($fonts -join "`n") + "`n" +
        "<style>" + $style + $contract + "</style>`n" +
        $body.Substring(0, $scriptAt) +
        "<script>`n" + $shim + "`n</script>`n" +
        $body.Substring($scriptAt) + "`n" +
        $tour

[IO.File]::WriteAllText("$PSScriptRoot\k631-live-demo.html", $page, $enc)
Write-Output ("Built k631-live-demo.html ({0:N0} KB)" -f ((Get-Item "$PSScriptRoot\k631-live-demo.html").Length / 1KB))
