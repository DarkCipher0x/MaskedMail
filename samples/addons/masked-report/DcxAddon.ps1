# DarkCipher add-on SDK (MaskedMail and ServerManagement) for Windows PowerShell 5.1 (API 2: encrypted bridge, ADR-035).
# Put DcxAddon.ps1 and DcxBridge.dll next to your script and dot-source it:   . "$PSScriptRoot\DcxAddon.ps1"
# Every message to and from the app is AES-256-GCM with keys made fresh for each run (ECDH P-256); see DcxBridge.cs.
Add-Type -Path (Join-Path $PSScriptRoot 'DcxBridge.dll')
$script:DcxCallId = 0

# Runs the handshake and returns the app's "invoke" message (params.command, params.context).
function Connect-Dcx { return ([DcxBridge]::Connect() | ConvertFrom-Json) }

# Sends one message object.
function Send-Dcx($Message) { [DcxBridge]::Write(($Message | ConvertTo-Json -Compress -Depth 8)) }

# Calls an app method and returns its result; throws on an error reply.
function Invoke-Dcx([string]$Method, $Params = @{}) {
    $script:DcxCallId++
    Send-Dcx @{ jsonrpc = '2.0'; id = $script:DcxCallId; method = $Method; params = $Params }
    $line = [DcxBridge]::Read()
    if ($null -eq $line) { throw 'the app closed the bridge' }
    $reply = $line | ConvertFrom-Json
    if ($reply.error) { throw "$Method failed: $($reply.error.message)" }
    return $reply.result
}

# Everything this add-on can call: .methods, each with the permission it needs and whether that is granted.
# MaskedMail: aliases.list / create / setEnabled / update, mail.list / get, queue.add, mail.send, routing.sweep,
# files.pick, ui.notify, app.info (see docs/ADDONS.md). Changes and sends are shown to the user, who allows or denies them.
function Get-DcxCapabilities { return (Invoke-Dcx 'app.capabilities') }

# ServerManagement only: runs an app capability by name, e.g.  Invoke-DcxTool 'host_overview' @{ host = 'web-1' }
function Invoke-DcxTool([string]$Tool, $Arguments = @{}) { return (Invoke-Dcx 'app.call' @{ tool = $Tool; args = $Arguments }).result }

# Adds a line to the run log.
function Write-DcxLog([string]$Text) { Send-Dcx @{ jsonrpc = '2.0'; method = 'log'; params = @{ text = $Text } } }

# Ends the run with a result (shown to the user when -Show).
function Complete-Dcx([string]$Text, [switch]$Show) { Send-Dcx @{ jsonrpc = '2.0'; method = 'done'; params = @{ text = $Text; show = [bool]$Show } } }
