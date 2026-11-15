"""
Builds the SAVE-preset icon: the Material Design Icons `floppy` glyph with a plus badge at its lower-right corner.

    pip install svgpathtools shapely
    python Scripts/make_save_icon.py        # prints the path data that goes in CleanPrint.App/Themes/Icons.xaml as IconSave

The icon pack has no floppy-with-plus, so it is built from the pack's own `floppy` shape by geometry: a round gap is cut out of the disk's
lower-right corner, a plus is placed in the gap, and the sharp slivers the cut would leave where the circle meets the walls are rounded
off (only in the neighbourhood of the cut — everywhere else the shape is the pack's floppy, point for point).

The result is written as one flat path of straight segments ("M x y L ... Z" rings): curves are sampled so finely (40 points per
segment) that no straight piece is visible even at 256 px. Outer rings and holes wind in opposite directions, so Icons.xaml draws it with
the non-zero fill rule (the "F1" prefix on every icon there).
"""
from svgpathtools import parse_path
from shapely.geometry import Polygon, Point, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

# MDI "floppy" (Apache-2.0, https://github.com/Templarian/MaterialDesign-SVG), 24x24 grid.
FLOPPY = ("M5,3A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5.5L18.5,3H17V9A1,1 0 0,1 16,10H8A1,1 0 0,1 7,9V3H5"
          "M12,4V9H15V4H12M7,12H17A1,1 0 0,1 18,13V19H6V13A1,1 0 0,1 7,12Z")

def flatten(subpath, per_curve=40):
    points = []
    for segment in subpath:
        steps = 1 if type(segment).__name__ == 'Line' else per_curve
        for k in range(steps):
            z = segment.point(k / steps)
            points.append((z.real, z.imag))
    return points

def plus(cx, cy, arm, thickness):
    return unary_union([box(cx - arm, cy - thickness / 2, cx + arm, cy + thickness / 2),
                        box(cx - thickness / 2, cy - arm, cx + thickness / 2, cy + arm)])

def build(cx=18.0, cy=18.0, gap_radius=5.7, arm=3.9, thickness=2.4, soften=0.45):
    outline, shutter, label_window = (Polygon(flatten(s)) for s in parse_path(FLOPPY).continuous_subpaths())
    floppy = unary_union([outline.difference(label_window), shutter])

    gap = Point(cx, cy).buffer(gap_radius, 64)
    cut = floppy.difference(gap)
    near = gap.buffer(2.2, 64)
    opened = cut.buffer(-soften, 32).buffer(soften, 32)   # shrink then grow: removes spikes narrower than 2 x soften, rounds sharp tips
    return unary_union([cut.difference(near), opened.intersection(near), plus(cx, cy, arm, thickness)]).buffer(0)

def to_path_data(geometry):
    polygons = list(geometry.geoms) if hasattr(geometry, 'geoms') else [geometry]
    rings = []
    for polygon in polygons:
        polygon = orient(polygon, 1.0)
        for ring in [polygon.exterior, *polygon.interiors]:
            coords = list(ring.coords)[:-1]
            rings.append("M " + " L ".join(f"{x:.3f} {y:.3f}" for x, y in coords) + " Z")
    return " ".join(rings)

if __name__ == '__main__':
    print(to_path_data(build()))
