using System.Collections.Generic;

namespace CleanPrint.Core.Models;

/// <summary>
/// An area painted white in the base-image editor: either a rectangle filled in one go, or a stroke of the eraser brush. They are recorded in the pixels of the turned canvas (the coordinates the
/// editor's stage uses) and painted onto the page after it has been turned and cropped. Immutable, so the editor's undo history can share them between states.
/// </summary>
public abstract record EraseShape;

/// <summary>A rectangle filled with white.</summary>
public sealed record EraseRect(double X, double Y, double Width, double Height) : EraseShape;

/// <summary>A path of the round eraser brush: every point along it, and the brush's radius, in canvas pixels.</summary>
public sealed record EraseStroke(IReadOnlyList<GeoPoint> Points, double Radius) : EraseShape;
