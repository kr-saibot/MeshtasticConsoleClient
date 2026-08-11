<#
  Creates emoji-replacements.xml from the official Unicode Emoji Test data.

  Only fully-qualified RGI emoji are included. Entries containing a skin-tone
  modifier are omitted, matching Unicode's "Full Emoji List" chart.
#>
[CmdletBinding()]
param(
    [string] $SourceUrl = 'https://www.unicode.org/Public/emoji/latest/emoji-test.txt',
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\emoji-replacements.xml')
)

$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$source = (New-Object Net.WebClient).DownloadString($SourceUrl)
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$currentCategory = ''
$currentSubCategory = ''
$entries = foreach ($line in ($source -split '\r?\n')) {
    if ($line -match '^#\s*group:\s*(.+?)\s*$') {
        $currentCategory = $matches[1].Trim()
        $currentSubCategory = ''
        continue
    }
    if ($line -match '^#\s*subgroup:\s*(.+?)\s*$') {
        $currentSubCategory = $matches[1].Trim()
        continue
    }
    if ($line -notmatch '^\s*([0-9A-F ]+)\s*;\s*fully-qualified\s*#\s*.+?\s+E[0-9.]+\s+(.+?)\s*$') { continue }

    $codePoints = $matches[1].Trim() -split '\s+'
    # The linked Full Emoji List deliberately does not contain skin-tone variants.
    if (@($codePoints | Where-Object { [Convert]::ToInt32($_, 16) -ge 0x1F3FB -and [Convert]::ToInt32($_, 16) -le 0x1F3FF }).Count -gt 0) { continue }

    $value = -join ($codePoints | ForEach-Object { [char]::ConvertFromUtf32([Convert]::ToInt32($_, 16)) })
    if ($seen.Add($value)) {
        [PSCustomObject]@{
            Value = $value
            Text = '[:' + $matches[2].Trim() + ':]'
            Category = $currentCategory
            SubCategory = $currentSubCategory
        }
    }
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteComment('Generated from Unicode emoji-test.txt. Fully-qualified RGI emoji; skin-tone variants omitted. Category fields follow Unicode group and subgroup.')
    $writer.WriteStartElement('EmojiReplacements')
    foreach ($entry in $entries) {
        $writer.WriteStartElement('Emoji')
        $writer.WriteAttributeString('value', $entry.Value)
        $writer.WriteAttributeString('text', $entry.Text)
        $writer.WriteAttributeString('category', $entry.Category)
        $writer.WriteAttributeString('subCategory', $entry.SubCategory)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
}

Write-Host ('Created {0} emoji entries in {1}' -f $entries.Count, $OutputPath)
