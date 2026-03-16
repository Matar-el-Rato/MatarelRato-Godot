"""
Applies exported_markers.tscn_snippet into Scenes/Tablero.tscn,
replacing the Positions, HomePositions, and BasePositions sections
while preserving everything else (header, resources, root node, etc).

1. Strips existing marker sections from Tablero.tscn
2. Inserts snippet content with corrected parent refs and unique_id formatting
"""

import re
import os
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))

SNIPPET_PATH = os.path.join(SCRIPT_DIR, "exported_markers.tscn_snippet")
TABLERO_PATH = os.path.join(PROJECT_ROOT, "Scenes", "Tablero.tscn")

# Container names and the parent ref their children need
CONTAINERS = {
    "Positions": "Positions",
    "HomePositions": "HomePositions",
    "BasePositions": "BasePositions",
}


def fix_snippet_line(line, current_container):
    """Fix a single snippet line for tscn compatibility."""
    # Fix quoted unique_ids: unique_id="200000010" -> unique_id=200000010
    line = re.sub(r'unique_id="(\d+)"', r'unique_id=\1', line)

    # Fix parent on child Marker3D nodes: parent="." -> parent="Positions" etc.
    if current_container and 'type="Marker3D"' in line and 'parent="."' in line:
        line = line.replace('parent="."', f'parent="{current_container}"')

    return line


def parse_and_fix_snippet(snippet_text):
    """Parse snippet into fixed lines grouped by container section."""
    sections = {}
    current = None
    lines = []

    for raw_line in snippet_text.splitlines():
        # Detect container headers
        m = re.match(r'\[node name="(\w+)" type="Node3D"', raw_line)
        if m and m.group(1) in CONTAINERS:
            if current:
                sections[current] = lines
            current = m.group(1)
            lines = [fix_snippet_line(raw_line, None)]
            continue

        if current is not None:
            lines.append(fix_snippet_line(raw_line, current))

    if current:
        sections[current] = lines

    return sections


def find_section_start(tablero_lines):
    """Find the first line index of any container section."""
    for i, line in enumerate(tablero_lines):
        for name in CONTAINERS:
            if re.match(rf'\[node name="{name}" type="Node3D"', line):
                return i
    return None


def main():
    if not os.path.exists(SNIPPET_PATH):
        print(f"Error: Snippet not found at {SNIPPET_PATH}")
        print("Run the BoardPosEditor export first.")
        sys.exit(1)

    if not os.path.exists(TABLERO_PATH):
        print(f"Error: Tablero.tscn not found at {TABLERO_PATH}")
        sys.exit(1)

    with open(SNIPPET_PATH, "r") as f:
        snippet_text = f.read()

    with open(TABLERO_PATH, "r") as f:
        tablero_text = f.read()

    # Parse and fix snippet
    sections = parse_and_fix_snippet(snippet_text)
    missing = [name for name in CONTAINERS if name not in sections]
    if missing:
        print(f"Error: Snippet is missing sections: {missing}")
        sys.exit(1)

    # Find where to cut the Tablero — everything before the first container is kept
    tablero_lines = tablero_text.splitlines()
    cut_index = find_section_start(tablero_lines)
    if cut_index is None:
        print("Error: Could not find Positions/HomePositions/BasePositions in Tablero.tscn")
        sys.exit(1)

    # Keep header (everything before marker sections)
    header = tablero_lines[:cut_index]

    # Build new file: header + all three sections
    new_lines = list(header)
    for name in ["Positions", "HomePositions", "BasePositions"]:
        section = sections[name]
        # Strip trailing blank lines, add one separator
        while section and section[-1].strip() == "":
            section.pop()
        new_lines.extend(section)
        new_lines.append("")

    output = "\n".join(new_lines) + "\n"
    with open(TABLERO_PATH, "w") as f:
        f.write(output)

    marker_count = sum(1 for l in output.splitlines() if 'type="Marker3D"' in l)
    print(f"Applied snippet to {TABLERO_PATH}")
    print(f"  Removed old sections (from line {cut_index + 1})")
    print(f"  Wrote {marker_count} markers across 3 sections")
    print(f"  Header preserved ({cut_index} lines)")


if __name__ == "__main__":
    main()
