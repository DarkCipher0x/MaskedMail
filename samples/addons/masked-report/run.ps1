# Sample add-on for DarkCipher MaskedMail (API 2: encrypted bridge). Protocol: docs/ADDONS.md.
# Runs at Low integrity: the only writable folder is $env:DCX_ADDON_DATA.
# DcxAddon.ps1 + DcxBridge.dll (samples/addons/sdk) do the encryption: every message to and from the app is AES-256-GCM.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DcxAddon.ps1"

try {
    $invoke = Connect-Dcx
    switch ($invoke.params.command) {
        'summary' {
            $aliases = @(Invoke-Dcx 'aliases.list')
            $lines = $aliases | Group-Object service | Sort-Object Count -Descending | ForEach-Object {
                $on = @($_.Group | Where-Object { $_.enabled }).Count
                $routed = @($_.Group | Where-Object { $_.routed }).Count
                "  $($_.Name): $($_.Count) ($on enabled, $routed with a folder)"
            }
            Complete-Dcx ("You have $($aliases.Count) masked address(es):`n" + ($lines -join "`n")) -Show
        }
        'latest' {
            $email = $invoke.params.context.alias.email
            $list = Invoke-Dcx 'mail.list' @{ folder = $email; limit = 5 }
            if ($list.total -eq 0) { Complete-Dcx "No mail in the folder for $email yet." -Show; break }
            $rows = $list.messages | ForEach-Object { "  $($_.received)  $($_.from -join ', ')`n    $($_.subject)" }
            Complete-Dcx ("Newest of $($list.total) message(s) for ${email}:`n" + ($rows -join "`n")) -Show
        }
        'queue-test' {
            $email = $invoke.params.context.alias.email
            $r = Invoke-Dcx 'queue.add' @{ from = $email; to = @($email); subject = 'Test from the Masked address report add-on'; text = "This test email was queued by an add-on.`nIt is sent only when you send the queue." }
            Complete-Dcx "Queued a test email from $email to itself. Open the outgoing queue to send it ($($r.queued) waiting)." -Show
        }
        'capabilities' {
            $caps = Get-DcxCapabilities
            $rows = $caps.methods | ForEach-Object { "  $($_.name)  [$(if ($_.permission) { $_.permission } else { 'no permission' })$(if ($_.granted) { '' } else { ', not granted' })]" }
            Complete-Dcx ("Methods this add-on can call:`n" + ($rows -join "`n")) -Show
        }
        default { Complete-Dcx "Unknown command $($invoke.params.command)" -Show }
    }
}
catch {
    try { Complete-Dcx ("The add-on failed: " + $_.Exception.Message) -Show } catch { }
    exit 1
}
