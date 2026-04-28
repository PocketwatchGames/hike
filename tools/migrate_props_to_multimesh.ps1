# Migrates scenes/props/*.tscn from the per-prop LitSprite path to the
# multimesh-batched MultimeshPropSprite path.
#
# Mechanical transformation, run once after the multimesh-prop scaffolding
# is in place. Idempotent — re-running on already-migrated files is a no-op.
# Skips files that don't reference LitSprite (already migrated, or never used
# the LitSprite path to begin with).
#
# What it does for each .tscn that imports LitSprite.cs:
#   - Replace the LitSprite ext_resource with a MultimeshPropSprite ext_resource
#     (changes path, uid, and local id "3_litsprite" → "3_mmprop").
#   - Update the Sprite3D node's `script = ExtResource(...)` to match the new id.
#   - Drop unused ext_resources (sprite_lit, sprite_shadow_caster,
#     sprite_reflection, ao_blob) — WorldPropScatter handles materials now.
#   - Drop obsolete LitSprite-only properties (Mirror, CenteredAtBase,
#     MaterialTemplate, ShadowCasterTemplate, ReflectionTemplate,
#     AoDecalTexture, alpha_cut).
#
# What it leaves alone: every other node in the scene (StaticBody3D,
# CollisionShape3D, Decal, GPUParticles3D, AudioStreamPlayer3D, Light3D,
# anything else authored as a side-car). The Sprite3D's centered/offset/
# pixel_size/texture/region_enabled/region_rect/ForwardOffset/ScaleMin/Max
# are kept verbatim so visual placement carries over.
#
# After running: open Godot once so the editor regenerates .uid files for
# any newly-altered .tscn. Then play-test a sample of migrated props.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsDir = Join-Path $repoRoot 'scenes\props'

if (-not (Test-Path $propsDir)) {
    Write-Error "Props directory not found: $propsDir"
    exit 1
}

$NEW_SCRIPT_UID = 'uid://7fmr4y8a6cto'
$NEW_SCRIPT_PATH = 'res://scripts/voxels/props/MultimeshPropSprite.cs'
$NEW_LOCAL_ID = '3_mmprop'

$migrated = 0
$skipped = 0
$failed = @()

Get-ChildItem -Path $propsDir -Filter '*.tscn' | ForEach-Object {
    $file = $_
    $text = Get-Content $file.FullName -Raw -Encoding UTF8

    # Skip already-migrated files (idempotency).
    if ($text -match [regex]::Escape($NEW_SCRIPT_PATH)) {
        $skipped += 1
        return
    }

    # Skip files that never used LitSprite.
    $litMatch = [regex]::Match($text, '\[ext_resource type="Script" uid="uid://[^"]+" path="res://scripts/gameplay/LitSprite\.cs" id="([^"]+)"\]')
    if (-not $litMatch.Success) {
        $skipped += 1
        return
    }

    try {
        $oldLocalId = $litMatch.Groups[1].Value

        # Replace LitSprite ext_resource line.
        $newExtResource = '[ext_resource type="Script" uid="' + $NEW_SCRIPT_UID + '" path="' + $NEW_SCRIPT_PATH + '" id="' + $NEW_LOCAL_ID + '"]'
        $text = $text.Replace($litMatch.Value, $newExtResource)

        # Update the Sprite3D node's script= reference to the new local id.
        $oldScriptRef = 'script = ExtResource("' + $oldLocalId + '")'
        $newScriptRef = 'script = ExtResource("' + $NEW_LOCAL_ID + '")'
        $text = $text.Replace($oldScriptRef, $newScriptRef)

        # Drop unused ext_resource lines. Trailing newline included so the file
        # doesn't accumulate blank lines.
        $patterns = @(
            '(?m)^\[ext_resource type="Material" path="res://resources/materials/sprite_lit\.tres" id="[^"]+"\]\r?\n',
            '(?m)^\[ext_resource type="ShaderMaterial" path="res://resources/materials/sprite_shadow_caster\.tres" id="[^"]+"\]\r?\n',
            '(?m)^\[ext_resource type="ShaderMaterial" path="res://resources/materials/sprite_reflection\.tres" id="[^"]+"\]\r?\n',
            '(?m)^\[ext_resource type="Texture2D" path="res://resources/materials/ao_blob\.tres" id="[^"]+"\]\r?\n'
        )
        foreach ($p in $patterns) {
            $text = [regex]::Replace($text, $p, '')
        }

        # Drop obsolete LitSprite-only properties from any node that had them.
        # MultimeshPropSprite has its own ForwardOffset / ScaleMin / ScaleMax /
        # AlignToTerrain / CastsShadow exports; those carry over verbatim if
        # they were authored.
        $obsoleteProps = @(
            '(?m)^Mirror = [^\r\n]*\r?\n',
            '(?m)^CenteredAtBase = [^\r\n]*\r?\n',
            '(?m)^MaterialTemplate = ExtResource\("[^"]+"\)\r?\n',
            '(?m)^ShadowCasterTemplate = ExtResource\("[^"]+"\)\r?\n',
            '(?m)^ReflectionTemplate = ExtResource\("[^"]+"\)\r?\n',
            '(?m)^AoDecalTexture = ExtResource\("[^"]+"\)\r?\n',
            '(?m)^alpha_cut = [^\r\n]*\r?\n'
        )
        foreach ($p in $obsoleteProps) {
            $text = [regex]::Replace($text, $p, '')
        }

        # Write back. Keep UTF8 (no BOM) to match how Godot writes .tscn files.
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($file.FullName, $text, $utf8NoBom)
        $migrated += 1
        Write-Host ("migrated " + $file.Name)
    }
    catch {
        $failed += $file.Name
        Write-Host ("FAILED " + $file.Name + ": " + $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host ("Migrated: " + $migrated)
Write-Host ("Skipped:  " + $skipped + " (already migrated or no LitSprite)")
if ($failed.Count -gt 0) {
    Write-Host ("Failed:   " + $failed.Count) -ForegroundColor Red
    $failed | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
}
