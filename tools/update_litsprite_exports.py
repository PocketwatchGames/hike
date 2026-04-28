#!/usr/bin/env python3
"""One-shot migration: wire LitSprite's MaterialTemplate / ShadowCasterTemplate /
ReflectionTemplate / AoDecalTexture as ExtResource references in every scene
that uses LitSprite.

Run from repo root: `python tools/update_litsprite_exports.py`. Idempotent —
re-running on already-updated scenes is a no-op (it skips nodes that already
have MaterialTemplate set).

The four [Export] properties were previously fallback-loaded via GD.Load in
LitSprite._Ready; they're now scene-wired per the project's [Export]
convention. Without this migration, every LitSprite would log a missing-
template error at runtime.
"""

import os
import re
import sys
from glob import glob

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Properties to add to every LitSprite node, with the resource path each
# should reference. The order matches the field declaration order in
# LitSprite.cs so a future reader can scan them top-to-bottom.
MATERIALS = [
    ("MaterialTemplate", "ShaderMaterial", "res://resources/materials/sprite_lit.tres"),
    ("ShadowCasterTemplate", "ShaderMaterial", "res://resources/materials/sprite_shadow_caster.tres"),
    ("ReflectionTemplate", "ShaderMaterial", "res://resources/materials/sprite_reflection.tres"),
    ("AoDecalTexture", "Texture2D", "res://resources/materials/ao_blob.tres"),
]

LITSPRITE_SCRIPT_PATH = "res://scripts/gameplay/LitSprite.cs"

EXT_RE = re.compile(
    r'\[ext_resource\s+(?P<attrs>[^\]]+)\]'
)
ATTR_RE = re.compile(r'(\w+)="([^"]*)"')


def parse_ext_resources(text):
    """Returns list of (full_match_text, attrs_dict, span)."""
    out = []
    for m in EXT_RE.finditer(text):
        attrs = dict(ATTR_RE.findall(m.group("attrs")))
        out.append((m.group(0), attrs, m.span()))
    return out


def find_litsprite_id(ext_resources):
    for _, attrs, _ in ext_resources:
        if attrs.get("path") == LITSPRITE_SCRIPT_PATH:
            return attrs["id"]
    return None


def find_existing_resource_id(ext_resources, path):
    for _, attrs, _ in ext_resources:
        if attrs.get("path") == path:
            return attrs["id"]
    return None


def next_id_prefix(ext_resources):
    """IDs in scene files look like `7_litsprite` or `8_bo3bm` — a number
    followed by underscore + name. Find the max prefix and start above it."""
    max_n = 0
    for _, attrs, _ in ext_resources:
        rid = attrs.get("id", "")
        m = re.match(r"(\d+)", rid)
        if m:
            max_n = max(max_n, int(m.group(1)))
    return max_n + 1


def update_scene(path):
    with open(path, "r", encoding="utf-8") as f:
        original = f.read()

    if LITSPRITE_SCRIPT_PATH not in original:
        return False

    text = original
    ext_resources = parse_ext_resources(text)
    litsprite_id = find_litsprite_id(ext_resources)
    if not litsprite_id:
        return False

    # Ensure each material resource is present in the ext_resource block.
    next_n = next_id_prefix(ext_resources)
    prop_to_id = {}
    new_ext_lines = []
    for prop, rtype, rpath in MATERIALS:
        existing = find_existing_resource_id(ext_resources, rpath)
        if existing:
            prop_to_id[prop] = existing
        else:
            new_id = f'{next_n}_lit_{prop.lower()}'
            next_n += 1
            new_ext_lines.append(
                f'[ext_resource type="{rtype}" path="{rpath}" id="{new_id}"]'
            )
            prop_to_id[prop] = new_id

    if new_ext_lines:
        # Insert new ext_resource lines immediately after the last existing one.
        # Re-parse so spans reflect any earlier mutation (none here yet, but
        # this keeps the function safe to extend).
        ext_resources = parse_ext_resources(text)
        last_end = ext_resources[-1][2][1]
        text = text[:last_end] + "\n" + "\n".join(new_ext_lines) + text[last_end:]

    # Now walk node blocks. A node header is `[node ...]`, properties follow
    # until the next `[` line. For every node whose body contains
    # `script = ExtResource("<litsprite_id>")`, insert the four MaterialTemplate
    # etc. lines right after that script line — but only if they aren't
    # already set.
    script_line = f'script = ExtResource("{litsprite_id}")'

    if script_line not in text:
        # Defensive: the LitSprite script is referenced but no node uses it.
        # Could happen if a scene declares the script as inheritance but no
        # instance has it set. Bail rather than touch unrelated lines.
        return False

    insertion = script_line
    for prop, _, _ in MATERIALS:
        insertion += f'\n{prop} = ExtResource("{prop_to_id[prop]}")'

    # Replace each occurrence of `script = ExtResource(<litsprite_id>)` with
    # the script line + four new property lines, BUT skip nodes that already
    # have MaterialTemplate set (idempotent re-run). To do that, locate each
    # node block individually.
    out_parts = []
    cursor = 0
    # Split on node headers so we can decide per-node.
    node_header_re = re.compile(r'^\[node\b[^\]]*\]', re.MULTILINE)
    headers = list(node_header_re.finditer(text))

    if not headers:
        return False

    out_parts.append(text[:headers[0].start()])
    for i, h in enumerate(headers):
        body_start = h.end()
        body_end = headers[i + 1].start() if i + 1 < len(headers) else len(text)
        body = text[body_start:body_end]

        if script_line in body and "MaterialTemplate" not in body:
            # First occurrence in this node body. Insert four property lines
            # immediately after the script line.
            body = body.replace(script_line, insertion, 1)

        out_parts.append(text[h.start():body_start])
        out_parts.append(body)

    new_text = "".join(out_parts)
    if new_text == original:
        return False

    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(new_text)
    return True


def main():
    scenes = glob(os.path.join(REPO_ROOT, "scenes", "**", "*.tscn"), recursive=True)
    updated = 0
    skipped = 0
    for path in scenes:
        try:
            changed = update_scene(path)
        except Exception as e:
            print(f"FAILED {path}: {e}", file=sys.stderr)
            continue
        if changed:
            updated += 1
        else:
            skipped += 1
    print(f"Updated {updated} scenes, skipped {skipped}.")


if __name__ == "__main__":
    main()
