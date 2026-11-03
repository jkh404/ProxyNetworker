$global:LASTEXITCODE = 0
if ($args -contains 'dump') {
    switch ($env:PN_UI_DUMP_FIXTURE_MODE) {
        'IdleFailure' { 'ERROR: could not get idle state.' }
        'MissingFilename' { 'UI hierchary dumped to: /data/local/tmp/old-ui.xml' }
        default { "UI hierchary dumped to: $($args[-1])" }
    }
}
elseif ($args -contains 'cat') {
    if ($env:PN_UI_DUMP_FIXTURE_MODE -eq 'InvalidXml') { '<invalid />' }
    else { '<hierarchy><node text="Fixture" class="TextBlock" package="test" /></hierarchy>' }
}
