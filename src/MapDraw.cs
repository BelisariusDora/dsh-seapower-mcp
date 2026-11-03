// MapDraw.cs - the plotting table's ink: POST /plot/draw, GET /plot/drawings, POST /plot/erase.
//
// What this adds over POST /plot/ruler (MapPlot.cs): that route builds a Noesis primitive and
// parents it to MapBase.Children. The game never saves it, the player can never edit it, and the
// eraser tool does not know it exists. This file writes into the game's own drawing model instead
// - SceneMapViewModel.DrawingLayer.Graphics - so a mark made here behaves like a hand-drawn one:
// the erase tool picks it, the right-click menu lists it, and it round-trips through the save.
//
// THE SAVE RELATIONSHIP is the part a caller must not be surprised by. PLOT_DRAW.md §8 followed
// SaveLoadManager.WriteMissionToFile -> WriteDrawingData with IL and a whole-assembly call-site
// scan: every object in Graphics is written into the mission .sav, and the auto-save timer reaches
// the same method (GameUpdater.update -> SaveLoadManager.OnUpdate -> SaveGame("_AutoSave.sav")).
// There is no temporary layer, no IsTemporary flag on IDrawing, and no second collection the map
// renders. So `savedIntoAutoSave` in the reply is not a setting we can switch off - it is what the
// game will do with the object we just added, and this file reports it instead of hiding it. The
// single exception is AngleDrawing, which falls through WriteDrawingData's final else branch (it
// logs "not supported drawing" and writes nothing), so for kind=angle that flag is reported false
// and the reply carries a saveNote.
//
// WHY Clear() IS NEVER CALLED. Graphics is a plain public ObservableCollection, so Clear() would
// compile and would work. It would also delete every mark the player ever drew, with no undo: the
// game has no history stack for drawings. The game assembly itself never clears the collection (no
// Clear/RemoveAt call site anywhere in the program - PLOT_DRAW.md §9.8), which is exactly why a
// bridge-side Clear would be the only thing in the system able to destroy player work in one call.
// Removal here is always by object reference, never by range, and an index-based removal says in
// its reply which type it removed because it cannot know who drew it.
//
// WHY PolygonDrawing IS WRITTEN THROUGH `Points` AND NEVER `Locations`. PolygonDrawing.Update()
// IL is literally Locations.Clear() followed by a re-projection of every entry in Points, and
// SceneMapViewModel.Update() calls DrawingLayer.Update() every frame (PLOT_DRAW.md §5.3). Anything
// written into Locations is therefore erased within one frame. Points is the input; Locations is a
// per-frame derived copy. The same input/derived split governs every other type here: Start/End,
// Center/Extremis, First/Vertex/Second and Points are inputs; StartPoint/EndPoint, CenterPoint,
// Radius, Locations and the ray collections are recomputed from them.
//
// A DRAWING THAT IS NOT COMPLETE MUST NOT REACH Graphics. MapDrawingLayer.Update() loops the
// collection and calls Update() on every entry with no try/catch, every frame. RingDrawing.Update()
// is the sharp edge: it dereferences Center.SnapPosition and Extremis.SnapPosition without a null
// check, so a ring added with one anchor missing throws sixty times a second and floods the game
// log. The other types do guard themselves, but the rule is uniform here anyway: when a required
// anchor could not be written the object is discarded and the reply says created:false with
// fieldsFailed, which is strictly better than a drawing the engine chokes on every frame.
//
// THE WIRE FORMAT IS SNAKE_CASE. The MCP layer sends `from_lat`/`from_lon`, `center_lat`/
// `center_lon`, `radius_nm` and `all_mine`; MapPlot's /plot/ruler reads camelCase, and the first
// written form of this contract described a nested `from:{lat,lon}`. Two spellings exist in the
// field, so both are accepted here and AliasOf refuses only a caller who sends the same field twice
// - a rename on either side then cannot turn into a silent 400 on the first live call.
//
// UNITS ARE NEVER IMPLIED. Ring radii carry radiusNmRequested beside radiusMetres and radiusDeg,
// because the game stores RingDrawing.Radius in DEGREES (GeoPosition.GetDistanceInDeg) while the
// request is in nautical miles. RulerDrawing.Distance is reported with no unit claim at all: its IL
// arithmetic (degrees * 60) reads as nautical miles while its member name says statute miles, and
// that conflict is reported rather than resolved by guessing.
//
// THE LEDGER IS SESSION-SCOPED. _oursIds/_ours/_oursLabels remember what this bridge drew so that
// erase can be precise, but they are process memory: after the mission is saved and reloaded (or
// the mission is switched) the game builds brand-new drawing objects from the save file and the
// ledger still points at the old ones. It is therefore only valid within the current session, and
// every read of it is checked against Graphics before it is trusted.
//
// Threading: all three entry points start with a Core.IsMain guard. ObservableCollection's
// CollectionChanged drives a Noesis ItemsControl directly, so an off-thread Add either throws or
// silently leaves the map unrendered (PLOT_DRAW.md §9.1).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    internal static class MapDraw
    {
        // ---------------------------------------------------------------- game type names
        // Resolved through GameReflect.FindType at call time rather than bound at compile time:
        // a strong `using SeapowerUI.ViewModels;` would make a rename in a future patch a build
        // failure of the whole bridge, and every property below is written reflectively anyway.
        private const string MarkerTypeName = "SeapowerUI.ViewModels.MarkerDrawing";
        private const string RulerTypeName = "SeapowerUI.ViewModels.RulerDrawing";
        private const string AngleTypeName = "SeapowerUI.ViewModels.AngleDrawing";
        private const string RingTypeName = "SeapowerUI.ViewModels.RingDrawing";
        private const string PolygonTypeName = "SeapowerUI.ViewModels.PolygonDrawing";
        private const string TextTypeName = "SeapowerUI.ViewModels.TextDrawing";
        private const string LocationTypeName = "MapControl.Location";
        private const string ProjectionTypeName = "MapControl.MapProjection";
        private const string GeoPositionTypeName = "SeaPower.GeoPosition";

        // ---------------------------------------------------------------- units and limits
        // WGS84 equatorial radius, the same value MapView.cs pins for its distance maths. The game
        // also exposes it as MapControl.MapProjection.Wgs84EquatorialRadius; EarthRadius() prefers
        // that and falls back to this constant, so a radius reported here matches the one the game
        // would use on this build.
        private const double EarthRadiusM = 6378137.0;
        private const double MetersPerDegree = Math.PI * EarthRadiusM / 180.0;
        private const double NauticalMileM = 1852.0;

        // A ring's radius arrives as nautical miles and leaves as an Extremis point; above 20000 nm
        // the destination maths walks over a pole and the ring stops meaning anything, so the input
        // is refused rather than silently wrapped.
        private const double MaxRadiusNm = 20000.0;

        // Distance from the ring centre to its Extremis point is walked due north (azimuth 0). The
        // game stores Radius as a scalar (GetDistanceInDeg of the pair), so the direction only
        // decides which point the radius is measured to - north keeps the longitude untouched and
        // makes the readback trivially checkable.
        private const double ExtremisAzimuthDeg = 0.0;

        // A self-check that disagrees with the requested radius by more than this is reported in
        // geometryNote instead of being treated as a failure: the point came from the game's own
        // projection, so a mismatch means the request approached a pole, not that the maths broke.
        private const double RadiusCheckTolerance = 0.01;

        private const double DefaultThickness = 2.0;
        private const double MinThickness = 0.5;
        private const double MaxThickness = 10.0;

        private const int MaxTextLength = 200;
        private const int MaxLabelLength = 64;

        // Request-shape caps, same spirit as MapPlot's MaxPoints: one HTTP call must not be able to
        // start an unbounded amount of engine work.
        private const int MaxPolygonPoints = 512;
        private const int MaxAnchorListItems = 64;

        // Assign.Fill: where an assigned value comes from. A GeoPosition is written into the drawing
        // itself; an "anchor" is a detached MarkerDrawing used only as an ISnapPoint, which is the
        // one ISnapPoint implementation we can synthesise from a coordinate (PLOT_DRAW.md §3).
        private const int FillFromPoint = 0;
        private const int FillFromAnchor = 1;

        // ---------------------------------------------------------------- request field sets
        private static readonly string[] DrawingKinds = new string[]
        {
            "marker", "ruler", "angle", "ring", "polygon", "text"
        };

        private static readonly string[] CommonFields = new string[]
        {
            "kind", "persist", "validate_only", "thickness", "label"
        };

        private static readonly string[] MarkerFields = new string[] { "lat", "lon", "unit" };

        // Two wire spellings are accepted for the same thing, and that is deliberate: the MCP layer
        // sends snake_case (`from_lat`, `center_lat`, `radius_nm`, `all_mine`) while MapPlot's
        // existing /plot/ruler and /plot/measure read camelCase, and the first written form of this
        // contract described a nested `from:{lat,lon}`. Listing both in the whitelist and refusing
        // "both at once" is what keeps a rename on either side from becoming a 400 in the field.
        private static readonly string[] RulerFields = new string[]
        {
            "from", "to", "fromUnit", "toUnit",
            "from_lat", "from_lon", "to_lat", "to_lon", "from_unit", "to_unit"
        };
        private static readonly string[] AngleFields = new string[] { "a", "vertex", "b" };
        private static readonly string[] RingFields = new string[]
        {
            "center", "center_lat", "center_lon", "radiusNm", "radius_nm"
        };
        private static readonly string[] PolygonFields = new string[] { "points" };
        private static readonly string[] TextFields = new string[] { "lat", "lon", "text" };

        private static readonly string[] EraseFields = new string[] { "id", "index", "all_mine", "kind" };
        private static readonly string[] ListFields = new string[] { "limit" };

        private static readonly string[] AllDrawFields = Union(CommonFields, MarkerFields,
            RulerFields, AngleFields, RingFields, PolygonFields, TextFields);

        // Per-kind allowed set. A field that belongs to a different kind is reported as unknown for
        // this kind instead of being ignored, so `{"kind":"marker","radiusNm":5}` cannot look like
        // a ring request that silently drew nothing.
        private static readonly Dictionary<string, string[]> KindFields = BuildKindFields();

        // ---------------------------------------------------------------- session ledger
        // Three parallel lists: the id we handed out, the live object, and the caller's label. Kept
        // parallel rather than as one record type because erase needs the id index and List needs
        // the object index, and both are tiny. Only ever touched on the Unity main thread, so no
        // locking: every entry point below starts with the Core.IsMain guard.
        private static readonly List<int> _oursIds = new List<int>();
        private static readonly List<object> _ours = new List<object>();
        private static readonly List<string> _oursLabels = new List<string>();

        // Ids start at 1 and are never reused, so a stale id in a caller's notes fails loudly
        // ("not in this session's ledger") instead of silently erasing a different drawing.
        private static int _nextId = 1;

        // ================================================================ entry points

        /// <summary>POST /plot/draw. Validates the request, computes the geometry, and - unless
        /// validate_only was set - builds the game object, writes its anchors, updates it once and
        /// appends it to DrawingLayer.Graphics.</summary>
        internal static JObject Draw(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread", "/plot/draw must run on the Unity main thread", false);

            string kind = RequireKind(req);
            RejectUnknown(req, "plot/draw", AllDrawFields);
            RejectForeignKindFields(req, kind);

            DrawReq r = new DrawReq();
            r.Kind = kind;
            r.ValidateOnly = OptionalBool(req, "validate_only", false);
            r.Persist = OptionalBool(req, "persist", true);
            r.Label = OptionalLabel(req);
            r.Thickness = OptionalNumber(req, "thickness", DefaultThickness);
            if (r.Thickness < MinThickness || r.Thickness > MaxThickness)
                throw new ApiException(400, "bad_value", "'thickness' must be within "
                    + MinThickness + ".." + MaxThickness + " pixels, got " + r.Thickness, false);

            // The drawing lands in the game's own DrawingLayer.Graphics, which the auto-save
            // serialises (see this file's header), so it is a write like /plot/course and HOLD
            // has to stop it. A dry run still runs: validate_only never reaches Materialize.
            if (CommsBridge.Hold && !r.ValidateOnly)
                throw new ApiException(409, "held",
                    "operator HOLD is on: plot drawings are refused until it is released", true);

            Plan plan; 
            if (kind == "marker") plan = PlanMarker(req);
            else if (kind == "ruler") plan = PlanRuler(req);
            else if (kind == "angle") plan = PlanAngle(req);
            else if (kind == "ring") plan = PlanRing(req);
            else if (kind == "polygon") plan = PlanPolygon(req);
            else plan = PlanText(req);

            return Finish(plan, r);
        }

        /// <summary>GET /plot/drawings. Reads the collection back in order, marks which entries this
        /// session drew, and does a best-effort read of each drawing's anchor coordinates.</summary>
        internal static JObject List(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread", "/plot/draw must run on the Unity main thread", false);

            // `limit` is the only query parameter (the MCP layer sends ?limit=N). Anything else is
            // refused, so a misspelled parameter cannot look like it was honoured.
            RejectUnknown(req, "plot/drawings", ListFields);
            int limit = OptionalWhole(req, "limit", 0);
            if (limit < 0)
                throw new ApiException(400, "bad_value",
                    "'limit' must be 0 (all) or greater, got " + limit, false);

            IList graphics = RequireGraphics();
            JArray drawings = new JArray();
            for (int i = 0; i < graphics.Count; i++)
            {
                if (limit > 0 && i >= limit) break;
                object d = graphics[i];
                JObject item = new JObject();
                item["index"] = i;
                item["type"] = ShortNameOfObject(d);

                int ledger = d == null ? -1 : LedgerIndexOf(d);
                item["ours"] = ledger >= 0;
                if (ledger >= 0)
                {
                    item["id"] = _oursIds[ledger];
                    if (_oursLabels[ledger] != null) item["label"] = _oursLabels[ledger];
                }

                string note;
                JToken anchors = AnchorsOf(d, out note);
                if (anchors == null)
                {
                    item["anchors"] = JValue.CreateNull();
                    item["anchorNote"] = note;
                }
                else
                {
                    item["anchors"] = anchors;
                }
                drawings.Add(item);
            }

            JObject o = new JObject();
            // count is the true total even when limit truncated the array: an index handed back by a
            // limited listing must stay valid for a later erase-by-index.
            o["count"] = graphics.Count;
            o["returned"] = drawings.Count;
            o["limited"] = limit > 0 && limit < graphics.Count;
            o["drawings"] = drawings;
            // Prune first, then publish: an id whose object the player already erased must not be
            // advertised as living here.
            o["ours"] = PruneLedger(graphics);
            JArray oursIds = new JArray();
            for (int i = 0; i < _oursIds.Count; i++) oursIds.Add(_oursIds[i]);
            o["oursIds"] = oursIds;
            return o;
        }

        /// <summary>POST /plot/erase. Exactly one of id / index / kind:"all-mine". Never a range
        /// clear, and never Repair-by-index guessing: the reply names what it removed.</summary>
        internal static JObject Erase(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread", "/plot/draw must run on the Unity main thread", false);

            RejectUnknown(req, "plot/erase", EraseFields);

            JToken idTok = Present(req, "id");
            JToken indexTok = Present(req, "index");
            JToken allTok = Present(req, "all_mine");
            JToken kindTok = Present(req, "kind");
            if (allTok != null)
            {
                if (allTok.Type != JTokenType.Boolean)
                    throw new ApiException(400, "bad_type", "'all_mine' must be true, got "
                        + allTok.Type.ToString(), false);
                // false is refused rather than treated as a no-op: a request that erases nothing is
                // much more likely to be a caller bug than an intention.
                if (!(bool)allTok)
                    throw new ApiException(400, "bad_value",
                        "'all_mine' must be true; omit it to erase nothing", false);
            }
            int given = 0;
            if (idTok != null) given++;
            if (indexTok != null) given++;
            if (allTok != null) given++;
            if (kindTok != null) given++;
            if (given == 0)
                throw new ApiException(400, "missing_field", "one of 'id', 'index' or 'all_mine':true"
                    + " is required for plot/erase", false);
            if (given > 1)
                throw new ApiException(400, "bad_value",
                    "give exactly one of 'id', 'index' or 'all_mine', got " + given + " of them", false);

            // Erasing destroys the operator's own annotations and there is no undo stack behind
            // them (see Clear()), so it is refused outright while HOLD is on - no dry-run form.
            if (CommsBridge.Hold)
                throw new ApiException(409, "held",
                    "operator HOLD is on: nothing is erased until it is released", true);

            IList graphics = RequireGraphics();
            JObject o = new JObject();
            JArray types = new JArray();
            JArray ids = new JArray();
            int removed = 0;
            int countBefore = graphics.Count;

            if (idTok != null)
            {
                int id = WholeField(idTok, "id");
                int ledger = _oursIds.IndexOf(id);
                if (ledger < 0)
                    throw new ApiException(400, "bad_index", "'id' " + id + " is not in this session's"
                        + " ledger: either it was never drawn by this bridge, or the mission was saved"
                        + " and reloaded (the ledger does not survive that)", false);
                object obj = _ours[ledger];
                if (obj == null || !graphics.Contains(obj))
                {
                    // The object left Graphics without us (player eraser, or a mission load rebuilt
                    // the collection). The ledger entry is dead; drop it and say so.
                    ForgetLedgerAt(ledger);
                    o["note"] = "id " + id + " was already gone from Graphics; the stale ledger entry"
                        + " was dropped and nothing was removed";
                }
                else
                {
                    string typeName = ShortNameOfObject(obj);
                    graphics.Remove(obj);
                    removed = 1;
                    types.Add(typeName);
                    ids.Add(id);
                    o["removedType"] = typeName;
                    ForgetLedgerAt(ledger);
                }
            }
            else if (indexTok != null)
            {
                int index = WholeField(indexTok, "index");
                if (index < 0 || index >= graphics.Count)
                    throw new ApiException(400, "bad_index", "'index' " + index
                        + " is out of range: Graphics holds " + graphics.Count + " drawing(s)", false);
                object obj = graphics[index];
                string typeName = ShortNameOfObject(obj);
                graphics.Remove(obj);
                removed = 1;
                types.Add(typeName);
                o["removedType"] = typeName;
                o["removedIndex"] = index;
                o["note"] = "index removal ignores provenance: the entry at index " + index
                    + " may have been drawn by the player, and whatever depended on it (a ruler end,"
                    + " a ring centre) now points at a detached object";
                int ledger = obj == null ? -1 : LedgerIndexOf(obj);
                if (ledger >= 0)
                {
                    ids.Add(_oursIds[ledger]);
                    ForgetLedgerAt(ledger);
                }
            }
            else
            {
                // Reached by all_mine:true and by the older kind:"all-mine" spelling.
                if (kindTok != null)
                {
                    if (kindTok.Type != JTokenType.String)
                        throw new ApiException(400, "bad_type", "'kind' must be the string \"all-mine\","
                            + " got " + kindTok.Type.ToString(), false);
                    string kindValue = (string)kindTok;
                    if (kindValue != "all-mine")
                        throw new ApiException(400, "bad_value",
                            "'kind' must be \"all-mine\", got \"" + kindValue + "\"", false);
                }
                // Walk the ledger backwards so removals keep the indices below them valid.
                for (int i = _oursIds.Count - 1; i >= 0; i--)
                {
                    object obj = _ours[i];
                    int keepId = _oursIds[i];
                    if (obj != null && graphics.Contains(obj))
                    {
                        string typeName = ShortNameOfObject(obj);
                        graphics.Remove(obj);
                        removed++;
                        types.Add(typeName);
                        ids.Add(keepId);
                    }
                    ForgetLedgerAt(i);
                }
            }

            int byDependency = (countBefore - graphics.Count) - removed;
            if (byDependency < 0) byDependency = 0;

            o["removed"] = removed;
            o["removedTypes"] = types;
            o["removedIds"] = ids;
            o["removedByDependency"] = byDependency;
            // The official eraser does more than this: it calls MapDrawingLayer.RemoveDependsOn(target),
            // which runs a DependsOnVisitor over the collection and takes dependent drawings along
            // (the anchor markers a ruler's ends hang on, for example). This route removes exactly
            // what it names, so anything that depended on the removed object keeps a live reference
            // to it and simply stops being drawn - the same state a deliberately hidden anchor is in.
            o["dependencyNote"] = "removal here is a bare collection removal, so removedByDependency is"
                + " normally 0. Nothing that referenced the removed drawing is removed with it: those"
                + " references stay valid and the figures built on them keep rendering, but their"
                + " anchor is no longer drawn or listed.";
            o["drawingCount"] = graphics.Count;
            o["remainingOurs"] = PruneLedger(graphics);
            return o;
        }

        // ================================================================ request planning
        // A Plan is the request turned into "which class, which constructor arguments, which
        // properties, which anchor coordinates" plus the geometry that goes into the reply. Planning
        // never touches the game: that is what lets validate_only return real numbers without
        // constructing anything.

        private sealed class Assign
        {
            public string[] Names;   // candidate member names, tried in order
            public int Fill;         // FillFromPoint or FillFromAnchor
            public Pt Point;
            public bool Required;    // a missing required anchor must keep the object out of Graphics
            public string What;      // key used in the reply's `fields` / `fieldsFailed`
        }

        private sealed class Plan
        {
            public string Kind;
            public string TypeName;
            public object[] CtorArgs;             // null means the parameterless constructor
            public List<Assign> Props = new List<Assign>();
            public List<Pt> PolygonPoints;        // polygon only: one detached marker per vertex
            public JObject Geometry = new JObject();
            public JToken Anchor;
            public string PrimaryWhat;            // which property the reply calls anchorProperty
            // Whether any coordinate came from a unit's live position. Drives anchorLiveness: a
            // unit-derived anchor is a snapshot unless something refreshes it, and the reply must
            // not imply tracking it cannot promise.
            public bool UsesUnit;
            // Kept as a number as well as a JSON value: the read-back cross-check needs to compare
            // it after construction, and pulling a double back out of a JToken would depend on
            // GameReflect.Num understanding JValue.
            public double AngleDeg = double.NaN;
        }

        private sealed class DrawReq
        {
            public string Kind;
            public bool ValidateOnly;
            public bool Persist;
            public double Thickness;
            public string Label;
        }

        /// <summary>One validated coordinate plus where it came from, so the reply can say whether a
        /// point was literal, looked up from a unit, or produced by the ring maths.</summary>
        private sealed class Pt
        {
            public double Lat;
            public double Lon;
            public string Source;
            public bool FromUnit;
        }

        private static Plan PlanMarker(JObject req)
        {
            Pt point = MarkerPoint(req);

            Plan plan = new Plan();
            plan.Kind = "marker";
            plan.TypeName = MarkerTypeName;
            plan.CtorArgs = null;
            plan.UsesUnit = point.FromUnit;
            plan.Props.Add(PointAssign("Location", point, true));
            plan.Anchor = PtToken(point);
            plan.PrimaryWhat = "Location";
            plan.Geometry["point"] = PtTokenWithSource(point);
            return plan;
        }

        private static Plan PlanText(JObject req)
        {
            Pt point = LatLonPoint(req, "kind 'text'");
            string text = RequireText(req);

            Plan plan = new Plan();
            plan.Kind = "text";
            plan.TypeName = TextTypeName;
            // TextDrawing has no parameterless constructor at all (ildump: `Void .ctor(String text)`
            // is its only constructor), so the text goes in through the constructor and the reply's
            // `fields` cannot claim we wrote a Text property afterwards.
            plan.CtorArgs = new object[] { text };
            plan.Props.Add(PointAssign("Location", point, true));
            plan.Anchor = PtToken(point);
            plan.PrimaryWhat = "Location";
            plan.Geometry["point"] = PtTokenWithSource(point);
            plan.Geometry["text"] = text;
            plan.Geometry["textLength"] = text.Length;
            plan.Geometry["textPassedTo"] = "the TextDrawing(String text) constructor";
            return plan;
        }

        private static Plan PlanRuler(JObject req)
        {
            Pt from = RulerPoint(req, "from", "from_lat", "from_lon", "fromUnit", "from_unit");
            Pt to = RulerPoint(req, "to", "to_lat", "to_lon", "toUnit", "to_unit");

            Plan plan = new Plan();
            plan.Kind = "ruler";
            plan.TypeName = RulerTypeName;
            plan.CtorArgs = null;
            plan.UsesUnit = from.FromUnit || to.FromUnit;
            // Start/End, not StartPoint/EndPoint. The latter pair has a *private* setter and
            // set_StartPoint is what derives StartPoint, EndPoint, MidPoint, Heading, Distance and
            // the RulerPoints collection (IL read). Writing StartPoint reflectively would "succeed"
            // (SetValue ignores the accessor's visibility) and leave Start/End null - a ruler that
            // never re-syncs to its anchors and renders nothing. So the geographic points travel as
            // detached MarkerDrawings, which is also what the game's own creation path does.
            plan.Props.Add(AnchorAssign("Start", from, true));
            plan.Props.Add(AnchorAssign("End", to, true));
            plan.Anchor = PtToken(from);
            plan.PrimaryWhat = "Start";

            plan.Geometry["from"] = PtTokenWithSource(from);
            plan.Geometry["to"] = PtTokenWithSource(to);
            double metres = DistanceMetres(from, to);
            if (!double.IsNaN(metres))
            {
                plan.Geometry["distanceMetres"] = metres;
                plan.Geometry["distanceNm"] = metres / NauticalMileM;
            }
            double heading = HeadingDeg(from, to);
            if (!double.IsNaN(heading)) plan.Geometry["headingDeg"] = heading;
            return plan;
        }

        private static Plan PlanAngle(JObject req)
        {
            Pt a = ParsePoint(Present(req, "a"), "a");
            Pt vertex = ParsePoint(Present(req, "vertex"), "vertex");
            Pt b = ParsePoint(Present(req, "b"), "b");

            Plan plan = new Plan();
            plan.Kind = "angle";
            plan.TypeName = AngleTypeName;
            plan.CtorArgs = null;
            // IL-confirmed input triple: First / Vertex / Second are the ISnapPoint anchors; Angle,
            // LabelPosition and the three ray collections are recomputed by Update() -> Recalculate().
            plan.Props.Add(AnchorAssign("First", a, true));
            plan.Props.Add(AnchorAssign("Vertex", vertex, true));
            plan.Props.Add(AnchorAssign("Second", b, true));
            plan.Anchor = PtToken(vertex);
            plan.PrimaryWhat = "Vertex";

            plan.Geometry["a"] = PtTokenWithSource(a);
            plan.Geometry["vertex"] = PtTokenWithSource(vertex);
            plan.Geometry["b"] = PtTokenWithSource(b);

            // Mirror of AngleDrawing.Recalculate() so validate_only can report the number the game
            // will store: two Vector2s built as (dLon, dLat) in degrees, then Vector2.SignedAngle,
            // then |signed| with a 180 -> 360-|signed| fold. The game's own guard is
            // sqrMagnitude < 1e-12 => angle 0, reproduced here.
            double dx1 = a.Lon - vertex.Lon;
            double dy1 = a.Lat - vertex.Lat;
            double dx2 = b.Lon - vertex.Lon;
            double dy2 = b.Lat - vertex.Lat;
            double sq1 = dx1 * dx1 + dy1 * dy1;
            double sq2 = dx2 * dx2 + dy2 * dy2;
            double angle;
            if (sq1 < 1e-12 || sq2 < 1e-12)
            {
                angle = 0.0;
            }
            else
            {
                double signed = Math.Atan2(dx1 * dy2 - dy1 * dx2, dx1 * dx2 + dy1 * dy2) * 180.0 / Math.PI;
                double magnitude = Math.Abs(signed);
                angle = magnitude > 180.0 ? 360.0 - magnitude : magnitude;
            }
            plan.Geometry["angleDeg"] = angle;
            plan.AngleDeg = angle;
            plan.Geometry["angleDegBasis"] = "mirror of AngleDrawing.Recalculate(): Vector2(dLon,dLat)"
                + " per ray, SignedAngle, |angle| folded into 0..180";
            return plan;
        }

        private static Plan PlanRing(JObject req)
        {
            Pt center = RingCenter(req);
            string radiusField;
            double radiusNm = RequireAliasNumber(req, new string[] { "radius_nm", "radiusNm" },
                "kind 'ring'", out radiusField);
            if (!(radiusNm > 0.0) || radiusNm > MaxRadiusNm)
                throw new ApiException(400, "bad_value", "'" + radiusField + "' must be within (0, "
                    + MaxRadiusNm + "] nautical miles, got " + radiusNm, false);

            double earthRadius = EarthRadius();
            double metres = radiusNm * NauticalMileM;
            string extremisNote;
            Pt extremis = ExtremisPoint(center, metres, earthRadius, out extremisNote);
            if (Math.Abs(extremis.Lat) > 90.0 || Math.Abs(extremis.Lon) > 180.0)
                throw new ApiException(400, "bad_value", "a " + radiusNm + " nm ring centred at "
                    + center.Lat + "," + center.Lon + " puts its Extremis point at " + extremis.Lat
                    + "," + extremis.Lon + ", outside the coordinate range; a ring that size cannot be"
                    + " expressed from this centre", false);

            Plan plan = new Plan();
            plan.Kind = "ring";
            plan.TypeName = RingTypeName;
            plan.CtorArgs = null;
            // RingDrawing.Update() derives CenterPoint and Radius from Center.SnapPosition and
            // Extremis.SnapPosition, so these two are the whole input. Both are required: Update()
            // dereferences them without a null check and it runs for every drawing every frame, so a
            // ring with a missing anchor would throw 60 times a second.
            plan.Props.Add(AnchorAssign("Center", center, true));
            plan.Props.Add(AnchorAssign("Extremis", extremis, true));
            plan.Anchor = PtToken(center);
            plan.PrimaryWhat = "Center";

            plan.Geometry["center"] = PtTokenWithSource(center);
            plan.Geometry["extremis"] = PtTokenWithSource(extremis);
            plan.Geometry["radiusNmRequested"] = radiusNm;
            plan.Geometry["radiusField"] = radiusField;
            plan.Geometry["radiusMetres"] = metres;
            plan.Geometry["earthRadiusMetres"] = earthRadius;

            double radiusDeg = FlatDegrees(center, extremis);
            if (!double.IsNaN(radiusDeg))
                plan.Geometry["radiusDeg"] = radiusDeg;

            // Independent check with the game's own great-circle routine: the Extremis point was
            // produced by MapControl.Location.GetLocation, so measuring the pair back with
            // GetDistance must return the requested distance. A mismatch is not a failure - it is
            // the signature of a radius that walked over a pole - so it is reported, not thrown.
            object centerGeo = GameReflect.Geo(center.Lat, center.Lon, 0.0);
            object extremisGeo = GameReflect.Geo(extremis.Lat, extremis.Lon, 0.0);
            double checkMetres = double.NaN;
            if (centerGeo != null && extremisGeo != null)
                checkMetres = GameReflect.Num(GameReflect.CallStatic(LocationTypeName, "GetDistance",
                    centerGeo, extremisGeo, earthRadius), double.NaN);
            if (!double.IsNaN(checkMetres))
            {
                plan.Geometry["checkDistanceMetres"] = checkMetres;
                double delta = Math.Abs(checkMetres - metres);
                plan.Geometry["checkDeltaMetres"] = delta;
                plan.Geometry["checkDeltaPct"] = metres > 0.0 ? delta / metres : 0.0;
                if (metres > 0.0 && delta / metres > RadiusCheckTolerance)
                    plan.Geometry["geometryNote"] = "the Extremis point is " + checkMetres
                        + " m from the centre while the request asked for " + metres
                        + " m (" + (delta / metres * 100.0) + "%). A due-north walk of this length"
                        + " passes a pole, so the game's own projection folded the destination back;"
                        + " the ring you get is that folded one. Use a smaller radiusNm, or a centre"
                        + " further from the pole.";
            }
            if (extremisNote != null) plan.Geometry["extremisNote"] = extremisNote;
            return plan;
        }

        private static Plan PlanPolygon(JObject req)
        {
            JToken raw = Present(req, "points");
            if (raw == null)
                throw new ApiException(400, "missing_field", "'points' is required for kind 'polygon'", false);
            JArray array = raw as JArray;
            if (array == null)
                throw new ApiException(400, "bad_type", "'points' must be an array of {lat,lon} objects, got "
                    + raw.Type.ToString(), false);
            if (array.Count < 3)
                throw new ApiException(400, "bad_value",
                    "'points' must contain at least 3 {lat,lon} objects, got " + array.Count, false);
            if (array.Count > MaxPolygonPoints)
                throw new ApiException(400, "bad_value", "'points' may contain at most "
                    + MaxPolygonPoints + " vertices, got " + array.Count, false);

            List<Pt> points = new List<Pt>(array.Count);
            for (int i = 0; i < array.Count; i++)
                points.Add(ParsePoint(array[i], "points[" + i + "]"));

            Plan plan = new Plan();
            plan.Kind = "polygon";
            plan.TypeName = PolygonTypeName;
            plan.CtorArgs = null;
            plan.PolygonPoints = points;
            plan.PrimaryWhat = "Points";

            JArray vertices = new JArray();
            double perimeter = 0.0;
            bool perimeterKnown = true;
            for (int i = 0; i < points.Count; i++)
            {
                vertices.Add(PtToken(points[i]));
                if (i > 0)
                {
                    double leg = DistanceMetres(points[i - 1], points[i]);
                    if (double.IsNaN(leg)) perimeterKnown = false;
                    else perimeter = perimeter + leg;
                }
            }
            plan.Anchor = vertices;
            plan.Geometry["points"] = vertices;
            plan.Geometry["pointCount"] = points.Count;
            if (perimeterKnown)
            {
                plan.Geometry["perimeterMetres"] = perimeter;
                plan.Geometry["perimeterNm"] = perimeter / NauticalMileM;
            }
            plan.Geometry["closingNote"] = "the game closes the outline itself; the last vertex is not"
                + " repeated as a closing point";
            return plan;
        }

        // ================================================================ construction

        private static JObject Finish(Plan plan, DrawReq r)
        {
            bool kindIsSaved = plan.Kind != "angle";

            JObject o = new JObject();
            o["kind"] = plan.Kind;
            o["created"] = false;
            o["validateOnly"] = r.ValidateOnly;
            o["savedIntoAutoSave"] = kindIsSaved;
            o["persisted"] = false;
            o["id"] = 0;
            o["drawingType"] = ShortName(plan.TypeName);
            if (plan.Anchor != null) o["anchor"] = plan.Anchor;
            o["thickness"] = r.Thickness;
            if (r.Label != null) o["label"] = r.Label;
            MergeInto(o, plan.Geometry);

            // What an anchor actually does. MarkerDrawing.Update() is empty (ildump), so a coordinate
            // taken from a unit is a snapshot; nothing in this reply should imply the mark will
            // follow the ship.
            o["anchorLiveness"] = plan.UsesUnit ? "snapshot-unverified" : "explicit-coordinates";
            if (plan.UsesUnit)
                o["anchorLivenessNote"] = "the coordinate came from a unit's live position at request"
                    + " time. MarkerDrawing.Update() has an empty body, so the mark stays where the"
                    + " ship was and does not follow it. Whether an ISnapPoint ever starts tracking"
                    + " depends on whoever refreshes MapUnitViewModel._location, and that caller was"
                    + " not found (stfld _location appears only inside set_Location), so tracking is"
                    + " not promised here.";

            if (!kindIsSaved)
            {
                o["saveNote"] = "AngleDrawing is the one IDrawing type the game does not save:"
                    + " SaveLoadManager.WriteDrawingData has no branch for it, falls into its final"
                    + " else and logs \"not supported drawing\". The angle survives until the mission"
                    + " is reloaded, then it is gone - and the warning will be in the game log.";
            }
            if (!r.Persist)
            {
                o["persistRequested"] = false;
                o["persistNote"] = "the game has no temporary drawing layer: IDrawing carries no"
                    + " temporary flag and DrawingLayer.Graphics is the only collection the map"
                    + " renders, so a drawing can only exist by being in it - and everything in it is"
                    + " written to the mission save. persist=false was therefore NOT honoured; the"
                    + " drawing below really is in Graphics. For a mark that must never enter a save,"
                    + " use POST /plot/ruler, which paints a visual-tree primitive instead.";
            }
            if (Math.Abs(r.Thickness - DefaultThickness) > 1e-9)
            {
                o["thicknessApplied"] = false;
                o["thicknessNote"] = "none of the IDrawing classes exposes a stroke width (member"
                    + " dumps of MarkerDrawing/RulerDrawing/AngleDrawing/RingDrawing/PolygonDrawing/"
                    + " TextDrawing have no Thickness or StrokeWidth), so 'thickness' is validated and"
                    + " echoed but cannot change what the game draws. Only POST /plot/ruler has a"
                    + " variable-width stroke (MapControl.Ruler.StrokeThickness).";
            }

            if (r.ValidateOnly)
            {
                o["validated"] = true;
                o["validatedNote"] = "no drawing object was constructed and nothing was added to"
                    + " Graphics; the coordinates above are the values a real call would use. The"
                    + " distance maths used MapControl.Location / GeoPosition helpers and allocated"
                    + " nothing that can be seen or saved.";
                // The count is still worth reporting - it is how a caller detects that the mission
                // changed between validate and draw - but it must not be the reason a validation
                // fails, so a missing host degrades to null instead of no_ui_host.
                IList graphics = GraphicsOrNull();
                if (graphics != null)
                {
                    o["drawingCount"] = graphics.Count;
                }
                else
                {
                    o["drawingCount"] = JValue.CreateNull();
                    o["drawingCountNote"] = "no drawing layer was reachable, so there was no count to"
                        + " read; a real call would fail with no_ui_host here";
                }
                return o;
            }

            Type type = GameReflect.FindType(plan.TypeName);
            if (type == null)
            {
                o["error"] = "type '" + plan.TypeName + "' is not loaded in this process, so the"
                    + " drawing model is unavailable; nothing was added to Graphics";
                o["drawingCount"] = JValue.CreateNull();
                return o;
            }

            IList target = RequireGraphics();
            int before = target.Count;

            object drawing = null;
            string constructError = null;
            try
            {
                if (plan.CtorArgs == null) drawing = Activator.CreateInstance(type);
                else drawing = Activator.CreateInstance(type, plan.CtorArgs);
            }
            catch (Exception ex)
            {
                constructError = Describe(ex);
            }
            if (drawing == null)
            {
                // Geometry was fine, the game refused the object. Report it as created:false rather
                // than a 500: the caller can tell the difference between a bad request and a build
                // where the drawing model changed shape.
                o["error"] = "the game's " + ShortName(plan.TypeName) + " could not be constructed"
                    + (constructError == null ? "" : ": " + constructError)
                    + "; nothing was added to Graphics";
                o["drawingCount"] = before;
                return o;
            }

            List<string> failures = new List<string>();
            JObject fields = new JObject();
            bool requiredOk = true;
            for (int i = 0; i < plan.Props.Count; i++)
            {
                Assign assign = plan.Props[i];
                object value = Materialize(assign, failures);
                if (value == null)
                {
                    if (assign.Required) requiredOk = false;
                    continue;
                }
                string used = SetFirst(drawing, assign.Names, value);
                if (used == null)
                {
                    failures.Add(assign.What + ": no writable member named "
                        + string.Join(" / ", assign.Names) + " on " + ShortName(plan.TypeName));
                    if (assign.Required) requiredOk = false;
                    continue;
                }
                fields[assign.What] = used;
            }

            if (plan.PolygonPoints != null)
            {
                object listObject = GameReflect.Member(drawing, "Points");
                IList points = listObject as IList;
                if (points == null)
                {
                    failures.Add("Points: PolygonDrawing.Points did not resolve to a list we can add to");
                    requiredOk = false;
                }
                else
                {
                    int added = 0;
                    for (int i = 0; i < plan.PolygonPoints.Count; i++)
                    {
                        Assign anchor = AnchorAssign("Location", plan.PolygonPoints[i], true);
                        anchor.What = "Points[" + i + "]";
                        object marker = Materialize(anchor, failures);
                        if (marker == null)
                        {
                            requiredOk = false;
                            continue;
                        }
                        try
                        {
                            points.Add(marker);
                            added++;
                        }
                        catch (Exception ex)
                        {
                            failures.Add(anchor.What + ": the vertex list rejected the anchor marker: "
                                + Describe(ex));
                            requiredOk = false;
                        }
                    }
                    if (added == plan.PolygonPoints.Count) fields["Points"] = "Points";
                }
            }

            if (!requiredOk)
            {
                o["error"] = "the required geometry could not be written (see fieldsFailed), so the"
                    + " object was NOT added to Graphics. RingDrawing is the type that punishes this"
                    + " hardest - Update() dereferences Center.SnapPosition and Extremis.SnapPosition"
                    + " with no null check, and MapDrawingLayer.Update() runs every drawing every"
                    + " frame - but the rule is the same for every kind: an incomplete object in the"
                    + " collection is a per-frame liability, and a save writer can catch it between"
                    + " two frames.";
                o["fieldsFailed"] = ToArray(failures);
                o["drawingCount"] = before;
                return o;
            }

            // One Update() before Add, on purpose: it fills the derived fields (Locations for a
            // polygon, Radius for a ring, the ray collections for an angle) so the object is
            // complete the first time anything looks at it. A half-built object inside Graphics is
            // exactly what makes WriteDrawingData persist "0,0" garbage and what a save writer
            // catching the object between two frames would see (PLOT_DRAW.md §9.6).
            string updateError = InvokeUpdate(drawing);

            string addError = null;
            try
            {
                target.Add(drawing);
            }
            catch (Exception ex)
            {
                addError = Describe(ex);
            }
            int after = target.Count;
            bool added2 = after == before + 1;
            if (!added2)
            {
                o["error"] = "Graphics did not grow after Add"
                    + (addError == null ? " (the collection accepted the call but not the object)" : ": " + addError)
                    + "; the drawing is not on the map";
                o["fieldsFailed"] = ToArray(failures);
                o["drawingCount"] = after;
                return o;
            }

            int id = _nextId;
            _nextId = _nextId + 1;
            _oursIds.Add(id);
            _ours.Add(drawing);
            _oursLabels.Add(r.Label);

            o["created"] = true;
            o["persisted"] = true;
            o["id"] = id;
            o["drawingCount"] = after;
            o["drawingIndex"] = after - 1;
            o["fields"] = fields;
            o["anchorProperty"] = fields[plan.PrimaryWhat] == null
                ? JValue.CreateNull()
                : fields[plan.PrimaryWhat];
            if (failures.Count > 0) o["fieldsFailed"] = ToArray(failures);
            if (updateError == null)
            {
                o["updateCalled"] = true;
            }
            else
            {
                o["updateError"] = updateError;
            }
            ReadBack(plan, drawing, o);
            // Both halves must hold for the reply to claim the drawing exists: the collection grew,
            // and the object survived its own Update(). Either one alone can be a lie (Add can be
            // followed by a throw in a CollectionChanged handler we cannot see).
            o["verified"] = added2 && updateError == null;
            if (failures.Count > 0 && updateError == null)
                o["note"] = "the drawing is in Graphics, but some optional fields could not be written"
                    + " (see fieldsFailed); what the game renders is whatever those defaults are";
            return o;
        }

        /// <summary>Turns an Assign into the value to write. A required anchor that cannot be built
        /// leaves a failure behind and returns null, which keeps the drawing out of Graphics.</summary>
        private static object Materialize(Assign assign, List<string> failures)
        {
            object geo = GameReflect.Geo(assign.Point.Lat, assign.Point.Lon, 0.0);
            if (geo == null)
            {
                failures.Add(assign.What + ": SeaPower.GeoPosition could not be constructed");
                return null;
            }
            if (assign.Fill == FillFromPoint) return geo;

            // A detached MarkerDrawing: MarkerDrawing implements ISnapPoint, and it is the only
            // ISnapPoint implementation that can be built from a bare coordinate. Deliberately never
            // added to Graphics - a hidden anchor cannot be erased by the player and cannot be
            // anchored onto by the player's own drawings (PLOT_DRAW.md §10.3).
            Type markerType = GameReflect.FindType(MarkerTypeName);
            if (markerType == null)
            {
                failures.Add(assign.What + ": the anchor type " + MarkerTypeName + " is not loaded");
                return null;
            }
            object marker = null;
            try { marker = Activator.CreateInstance(markerType); }
            catch (Exception ex)
            {
                failures.Add(assign.What + ": the anchor MarkerDrawing could not be constructed: "
                    + Describe(ex));
                return null;
            }
            if (marker == null)
            {
                failures.Add(assign.What + ": the anchor MarkerDrawing came back null");
                return null;
            }
            if (!GameReflect.SetMember(marker, "Location", geo))
            {
                failures.Add(assign.What + ": the anchor MarkerDrawing has no writable Location");
                return null;
            }
            return marker;
        }

        /// <summary>Reads the derived values the game computed, so the reply shows what the drawing
        /// actually holds rather than only what was requested.</summary>
        private static void ReadBack(Plan plan, object drawing, JObject o)
        {
            string kind = plan.Kind;
            if (kind == "ring")
            {
                // RingDrawing.Radius is set by Update() as GetDistanceInDeg(center, extremis, true):
                // a flat pythagorean distance in DEGREES, not nautical miles.
                double radiusDeg = GameReflect.Num(GameReflect.Member(drawing, "Radius"), double.NaN);
                if (!double.IsNaN(radiusDeg)) o["radiusDegFromGame"] = radiusDeg;
                Pt centerPoint = ReadPoint(GameReflect.Member(drawing, "CenterPoint"));
                if (centerPoint != null) o["centerPointFromGame"] = PtToken(centerPoint);
            }
            else if (kind == "ruler")
            {
                Pt start = ReadPoint(GameReflect.Member(drawing, "StartPoint"));
                Pt end = ReadPoint(GameReflect.Member(drawing, "EndPoint"));
                if (start != null) o["startPointFromGame"] = PtToken(start);
                if (end != null) o["endPointFromGame"] = PtToken(end);
                // Unit deliberately not asserted. The member is set by set_StartPoint from
                // GeoPosition.getDistanceInMiles, whose IL is GetDistanceInDeg * 60 - and 1 degree of
                // latitude is 60 nautical miles - while the member name says miles. The two readings
                // disagree, so the raw number is reported and the unit is not claimed. `distanceNm`
                // elsewhere in this reply is computed here from metres and is unaffected.
                double distance = GameReflect.Num(GameReflect.Member(drawing, "Distance"), double.NaN);
                if (!double.IsNaN(distance))
                {
                    o["distanceFromGame"] = distance;
                    o["distanceFromGameUnit"] = "unverified";
                    o["distanceFromGameNote"] = "RulerDrawing.Distance is derived from"
                        + " GeoPosition.getDistanceInMiles (IL: GetDistanceInDeg * 60). That arithmetic"
                        + " yields nautical miles while the member name says statute miles; neither"
                        + " the UI label nor a runtime comparison was checked, so no unit is asserted"
                        + " for this value.";
                }
                double heading = GameReflect.Num(GameReflect.Member(drawing, "Heading"), double.NaN);
                if (!double.IsNaN(heading)) o["headingFromGameDeg"] = heading;
            }
            else if (kind == "angle")
            {
                double angle = GameReflect.Num(GameReflect.Member(drawing, "Angle"), double.NaN);
                if (!double.IsNaN(angle))
                {
                    o["angleFromGameDeg"] = angle;
                    // plan.AngleDeg, not the JSON value: GameReflect.Num would hand back its fallback
                    // for a JToken, which would silently disable this cross-check.
                    if (!double.IsNaN(plan.AngleDeg) && Math.Abs(plan.AngleDeg - angle) > 0.01)
                        o["geometryNote"] = "angleFromGameDeg differs from the mirrored angleDeg by "
                            + Math.Abs(plan.AngleDeg - angle) + " degrees; the drawing shows the game's"
                            + " value, which means Recalculate() is not the formula read from its IL";
                }
                Pt label = ReadPoint(GameReflect.Member(drawing, "LabelPosition"));
                if (label != null) o["labelPositionFromGame"] = PtToken(label);
            }
            else if (kind == "polygon")
            {
                object locations = GameReflect.Member(drawing, "Locations");
                IList list = locations as IList;
                if (list != null) o["locationCountFromGame"] = list.Count;
            }
            else
            {
                Pt location = ReadPoint(GameReflect.Member(drawing, "Location"));
                if (location != null) o["locationFromGame"] = PtToken(location);
            }
        }

        // ================================================================ read-back helpers

        /// <summary>Best-effort anchor read for GET /plot/drawings. Returns null (plus a note
        /// explaining what was tried) rather than a made-up coordinate when nothing is readable.</summary>
        private static JToken AnchorsOf(object drawing, out string note)
        {
            note = null;
            if (drawing == null)
            {
                note = "the collection holds a null entry";
                return null;
            }

            string[] scalarNames = new string[]
            {
                "Location", "StartPoint", "EndPoint", "CenterPoint", "MidPoint", "TextPosition",
                "LabelPosition"
            };
            string[] collectionNames = new string[] { "Points", "Locations" };

            JArray found = new JArray();
            List<string> tried = new List<string>();
            for (int i = 0; i < scalarNames.Length; i++)
            {
                object value = GameReflect.Member(drawing, scalarNames[i]);
                if (value == null) continue;
                tried.Add(scalarNames[i]);
                Pt point = ReadPoint(value);
                if (point == null) continue;
                JObject item = PtToken(point);
                item["source"] = scalarNames[i];
                found.Add(item);
            }
            for (int i = 0; i < collectionNames.Length; i++)
            {
                IList list = GameReflect.Member(drawing, collectionNames[i]) as IList;
                if (list == null) continue;
                tried.Add(collectionNames[i]);
                for (int k = 0; k < list.Count && k < MaxAnchorListItems; k++)
                {
                    Pt point = ReadPoint(list[k]);
                    // A polygon vertex is an ISnapPoint, not a GeoPosition: reach through SnapPosition.
                    if (point == null) point = ReadPoint(GameReflect.Member(list[k], "SnapPosition"));
                    if (point == null) continue;
                    JObject item = PtToken(point);
                    item["source"] = collectionNames[i] + "[" + k + "]";
                    found.Add(item);
                }
            }

            if (found.Count == 0)
            {
                note = "no readable GeoPosition on " + drawing.GetType().Name + "; tried scalar members ("
                    + (tried.Count == 0 ? "none present" : string.Join(", ", tried.ToArray()))
                    + ") and the Points/Locations collections. A derived point stays at 0,0 until the"
                    + " owning drawing has been updated at least once, and a 0,0 reading is reported as"
                    + " 0,0 - it is real data, not a guess.";
                return null;
            }
            return found;
        }

        private static Pt ReadPoint(object value)
        {
            if (value == null) return null;
            double lat = GameReflect.Num(GameReflect.Member(value, "Latitude"), double.NaN);
            double lon = GameReflect.Num(GameReflect.Member(value, "Longitude"), double.NaN);
            if (double.IsNaN(lat) || double.IsNaN(lon)) return null;
            Pt point = new Pt();
            point.Lat = lat;
            point.Lon = lon;
            return point;
        }

        // ================================================================ game geometry

        /// <summary>The earth radius the game would use, falling back to the WGS84 constant so the
        /// maths below never silently degrades to a default.</summary>
        private static double EarthRadius()
        {
            double radius = GameReflect.Num(
                GameReflect.StaticMember(ProjectionTypeName, "Wgs84EquatorialRadius"), double.NaN);
            if (double.IsNaN(radius) || radius <= 0.0) return EarthRadiusM;
            return radius;
        }

        /// <summary>The ring's far point, produced by the game's own projection:
        /// MapControl.Location.GetLocation(azimuth, distance, earthRadius) is the destination-along-a-
        /// great-circle routine its IL implements (distance/earthRadius, then asin/atan2).
        /// The fallback exists only so a missing Location type still yields a usable coordinate, and
        /// it says so through extremisNote instead of pretending the game computed it.</summary>
        private static Pt ExtremisPoint(Pt center, double distanceMetres, double earthRadius, out string note)
        {
            note = null;
            Type locationType = GameReflect.FindType(LocationTypeName);
            object start = null;
            if (locationType != null)
            {
                try { start = Activator.CreateInstance(locationType, new object[] { center.Lat, center.Lon }); }
                catch (Exception) { start = null; }
            }
            if (start == null)
            {
                note = "MapControl.Location could not be constructed; the Extremis point was computed"
                    + " here as a due-north offset (radiusMetres / metres-per-degree) instead";
                return NorthOffset(center, distanceMetres);
            }
            object destination = GameReflect.Call(start, "GetLocation", ExtremisAzimuthDeg, distanceMetres,
                earthRadius);
            if (destination == null)
            {
                note = "MapControl.Location.GetLocation returned null; the Extremis point was computed"
                    + " here as a due-north offset instead";
                return NorthOffset(center, distanceMetres);
            }
            double lat = GameReflect.Num(GameReflect.Member(destination, "Latitude"), double.NaN);
            double lon = GameReflect.Num(GameReflect.Member(destination, "Longitude"), double.NaN);
            if (double.IsNaN(lat) || double.IsNaN(lon))
            {
                note = "the Location returned by GetLocation had no readable Latitude/Longitude; the"
                    + " Extremis point was computed here as a due-north offset instead";
                return NorthOffset(center, distanceMetres);
            }
            // A destination east/west of the antimeridian comes back outside -180..180; the game
            // normalises it when it draws, so the reported point is normalised the same way.
            double normalized = GameReflect.Num(
                GameReflect.CallStatic(LocationTypeName, "NormalizeLongitude", lon), lon);
            Pt point = new Pt();
            point.Lat = lat;
            point.Lon = normalized;
            point.Source = "MapControl.Location.GetLocation(azimuth " + ExtremisAzimuthDeg
                + " deg, " + distanceMetres + " m) from the centre";
            return point;
        }

        private static Pt NorthOffset(Pt center, double distanceMetres)
        {
            Pt point = new Pt();
            point.Lat = center.Lat + distanceMetres / MetersPerDegree;
            point.Lon = center.Lon;
            point.Source = "local due-north offset (radiusMetres / metres-per-degree)";
            return point;
        }

        /// <summary>Great-circle metres between two points, via the game's own routine; NaN when the
        /// game types are unavailable (the caller then omits the number instead of inventing one).</summary>
        private static double DistanceMetres(Pt a, Pt b)
        {
            if (a == null || b == null) return double.NaN;
            object geoA = GameReflect.Geo(a.Lat, a.Lon, 0.0);
            object geoB = GameReflect.Geo(b.Lat, b.Lon, 0.0);
            if (geoA == null || geoB == null) return double.NaN;
            return GameReflect.Num(GameReflect.CallStatic(LocationTypeName, "GetDistance",
                geoA, geoB, EarthRadius()), double.NaN);
        }

        private static double HeadingDeg(Pt origin, Pt target)
        {
            if (origin == null || target == null) return double.NaN;
            object from = GameReflect.Geo(origin.Lat, origin.Lon, 0.0);
            object to = GameReflect.Geo(target.Lat, target.Lon, 0.0);
            if (from == null || to == null) return double.NaN;
            return GameReflect.Num(GameReflect.CallStatic(LocationTypeName, "GetForwardAzimuth",
                from, to), double.NaN);
        }

        /// <summary>The flat degree distance RingDrawing.Radius is built from
        /// (GeoPosition.GetDistanceInDeg: sqrt(dLat^2 + dLon^2) in degrees, height ignored).</summary>
        private static double FlatDegrees(Pt a, Pt b)
        {
            if (a == null || b == null) return double.NaN;
            object geoA = GameReflect.Geo(a.Lat, a.Lon, 0.0);
            object geoB = GameReflect.Geo(b.Lat, b.Lon, 0.0);
            if (geoA == null || geoB == null) return double.NaN;
            return GameReflect.Num(GameReflect.CallStatic(GeoPositionTypeName, "GetDistanceInDeg",
                geoA, geoB, true), double.NaN);
        }

        // ================================================================ the game's collection

        // MainGameViewModel -> Map -> DrawingLayer -> Graphics. The same walk MapView.Read() does for
        // counting, kept in one place here. MapView.MainGame() is reused rather than reimplemented so
        // a plot command and a /map read can never disagree about which map object is the live one.

        /// <summary>Null instead of an exception: validate_only must be able to say "there was no
        /// count to read" without failing the request it was asked to validate.</summary>
        private static IList GraphicsOrNull()
        {
            object mainGame = MapView.MainGame();
            if (mainGame == null) return null;
            object map = GameReflect.Member(mainGame, "Map");
            if (map == null) return null;
            object layer = GameReflect.Member(map, "DrawingLayer");
            if (layer == null) return null;
            return GameReflect.Member(layer, "Graphics") as IList;
        }

        private static IList RequireGraphics()
        {
            IList graphics = GraphicsOrNull();
            if (graphics == null)
                throw new ApiException(409, "no_ui_host", "no drawing layer: is a mission loaded?", false);
            return graphics;
        }

        // ================================================================ ledger

        private static int LedgerIndexOf(object drawing)
        {
            for (int i = 0; i < _ours.Count; i++)
            {
                if (ReferenceEquals(_ours[i], drawing)) return i;
            }
            return -1;
        }

        private static void ForgetLedgerAt(int index)
        {
            _oursIds.RemoveAt(index);
            _ours.RemoveAt(index);
            _oursLabels.RemoveAt(index);
        }

        /// <summary>Drops ledger entries whose object has left Graphics and returns how many are still
        /// real. Doing this on every read is what keeps `ours`/`remainingOurs` honest after the player
        /// erases one of our drawings with the game's own eraser tool.</summary>
        private static int PruneLedger(IList graphics)
        {
            int kept = 0;
            for (int i = _oursIds.Count - 1; i >= 0; i--)
            {
                object drawing = _ours[i];
                if (drawing == null || !graphics.Contains(drawing)) ForgetLedgerAt(i);
                else kept++;
            }
            return kept;
        }

        // ================================================================ request helpers

        private static JToken Present(JObject req, string name)
        {
            if (req == null) return null;
            JToken token = req[name];
            if (token == null) return null;
            // An explicit JSON null counts as absent: "null" carries no intent we could honour, and
            // treating it as absent keeps one rule instead of two (same rule as MapPlot.Present).
            if (token.Type == JTokenType.Null) return null;
            return token;
        }

        private static void RejectUnknown(JObject req, string op, string[] allowed)
        {
            if (req == null) return;
            foreach (JProperty property in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length && !known; i++)
                {
                    if (allowed[i] == property.Name) known = true;
                }
                if (!known)
                    throw new ApiException(400, "unknown_field", "op '" + op + "' does not accept field '"
                        + property.Name + "'; allowed: " + DescribeFields(allowed), false);
            }
        }

        private static void RejectForeignKindFields(JObject req, string kind)
        {
            if (req == null) return;
            string[] allowed = FieldsForKind(kind);
            foreach (JProperty property in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length && !known; i++)
                {
                    if (allowed[i] == property.Name) known = true;
                }
                if (known) continue;
                throw new ApiException(400, "unknown_field", "op 'plot/draw' does not accept field '"
                    + property.Name + "' for kind '" + kind + "'; allowed: " + DescribeFields(allowed), false);
            }
        }

        private static string DescribeFields(string[] fields)
        {
            if (fields == null || fields.Length == 0) return "(none)";
            return string.Join(", ", fields);
        }

        private static string RequireKind(JObject req)
        {
            JToken token = Present(req, "kind");
            if (token == null)
                throw new ApiException(400, "missing_field", "'kind' is required for plot/draw; allowed: "
                    + string.Join(", ", DrawingKinds), false);
            if (token.Type != JTokenType.String)
                throw new ApiException(400, "bad_type", "'kind' must be a string, got "
                    + token.Type.ToString(), false);
            string kind = (string)token;
            for (int i = 0; i < DrawingKinds.Length; i++)
            {
                if (DrawingKinds[i] == kind) return kind;
            }
            throw new ApiException(400, "bad_value", "'kind' must be one of "
                + string.Join(", ", DrawingKinds) + ", got '" + kind + "'", false);
        }

        private static double NumberField(JToken token, string name)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new ApiException(400, "bad_type", "'" + name + "' must be a number, got "
                    + token.Type.ToString(), false);
            double value;
            try { value = token.Value<double>(); }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type", "'" + name + "' is not a valid number", false);
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ApiException(400, "bad_value", "'" + name + "' must be finite", false);
            return value;
        }

        private static double RequireNumber(JObject req, string name, string op)
        {
            JToken token = Present(req, name);
            if (token == null)
                throw new ApiException(400, "missing_field", "'" + name + "' is required for " + op, false);
            return NumberField(token, name);
        }

        private static double OptionalNumber(JObject req, string name, double fallback)
        {
            JToken token = Present(req, name);
            if (token == null) return fallback;
            return NumberField(token, name);
        }

        private static int OptionalWhole(JObject req, string name, int fallback)
        {
            JToken token = Present(req, name);
            if (token == null) return fallback;
            return WholeField(token, name);
        }

        /// <summary>First present alias, and an error when two spellings of the same field both
        /// appear. Returns null when none is present, so the caller owns the missing-field wording.</summary>
        private static JToken AliasOf(JObject req, string[] names, out string used)
        {
            used = null;
            JToken found = null;
            for (int i = 0; i < names.Length; i++)
            {
                JToken token = Present(req, names[i]);
                if (token == null) continue;
                if (found != null)
                    throw new ApiException(400, "bad_value", "'" + used + "' and '" + names[i]
                        + "' are the same field; give only one of them", false);
                found = token;
                used = names[i];
            }
            return found;
        }

        private static JToken AliasOf(JObject req, string[] names)
        {
            string used;
            return AliasOf(req, names, out used);
        }

        private static double RequireAliasNumber(JObject req, string[] names, string op, out string used)
        {
            JToken token = AliasOf(req, names, out used);
            if (token == null)
                throw new ApiException(400, "missing_field", "'" + names[0] + "' is required for "
                    + op, false);
            return NumberField(token, used);
        }

        /// <summary>Whole numbers only. A GET parameter such as ?unit=5 arrives as a JValue double, so
        /// an integral double is accepted and a fractional one is refused rather than truncated.</summary>
        private static int WholeField(JToken token, string name)
        {
            double value = NumberField(token, name);
            if (value != Math.Floor(value))
                throw new ApiException(400, "bad_type", "'" + name + "' must be a whole number, got "
                    + value, false);
            if (value < -2147483648.0 || value > 2147483647.0)
                throw new ApiException(400, "bad_value", "'" + name + "' is out of integer range", false);
            return (int)value;
        }

        private static bool OptionalBool(JObject req, string name, bool fallback)
        {
            JToken token = Present(req, name);
            if (token == null) return fallback;
            if (token.Type != JTokenType.Boolean)
                throw new ApiException(400, "bad_type", "'" + name + "' must be true or false, got "
                    + token.Type.ToString(), false);
            return (bool)token;
        }

        private static string OptionalLabel(JObject req)
        {
            JToken token = Present(req, "label");
            if (token == null) return null;
            if (token.Type != JTokenType.String)
                throw new ApiException(400, "bad_type", "'label' must be a string, got "
                    + token.Type.ToString(), false);
            string label = (string)token;
            if (label.Length == 0)
                throw new ApiException(400, "bad_value", "'label' must not be empty", false);
            if (label.Length > MaxLabelLength)
                throw new ApiException(400, "bad_value", "'label' may be at most " + MaxLabelLength
                    + " characters, got " + label.Length, false);
            return label;
        }

        private static string RequireText(JObject req)
        {
            JToken token = Present(req, "text");
            if (token == null)
                throw new ApiException(400, "missing_field", "'text' is required for kind 'text'", false);
            if (token.Type != JTokenType.String)
                throw new ApiException(400, "bad_type", "'text' must be a string, got "
                    + token.Type.ToString(), false);
            string text = (string)token;
            if (text.Trim().Length == 0)
                throw new ApiException(400, "bad_value", "'text' must not be empty or whitespace", false);
            if (text.Length > MaxTextLength)
                throw new ApiException(400, "bad_value", "'text' may be at most " + MaxTextLength
                    + " characters, got " + text.Length, false);
            return text;
        }

        private static void CheckLatLon(double lat, double lon, string label)
        {
            if (lat < -90.0 || lat > 90.0)
                throw new ApiException(400, "bad_value",
                    label + " latitude must be within -90..90, got " + lat, false);
            if (lon < -180.0 || lon > 180.0)
                throw new ApiException(400, "bad_value",
                    label + " longitude must be within -180..180, got " + lon, false);
        }

        private static Pt ParsePoint(JToken token, string label)
        {
            if (token == null || token.Type == JTokenType.Null)
                throw new ApiException(400, "missing_field", "'" + label + "' is required", false);
            JObject obj = token as JObject;
            if (obj == null)
                throw new ApiException(400, "bad_type", "'" + label + "' must be an object {lat,lon}, got "
                    + token.Type.ToString(), false);
            foreach (JProperty property in obj.Properties())
            {
                if (property.Name != "lat" && property.Name != "lon")
                    throw new ApiException(400, "unknown_field", "'" + label
                        + "' does not accept field '" + property.Name + "' (allowed: lat, lon)", false);
            }
            Pt point = new Pt();
            point.Lat = RequireNumber(obj, "lat", label);
            point.Lon = RequireNumber(obj, "lon", label);
            CheckLatLon(point.Lat, point.Lon, label);
            point.Source = "explicit lat/lon in '" + label + "'";
            return point;
        }

        private static Pt LatLonPoint(JObject req, string op)
        {
            JToken latToken = Present(req, "lat");
            JToken lonToken = Present(req, "lon");
            if (latToken == null || lonToken == null)
                throw new ApiException(400, "missing_field", "'lat' and 'lon' are required for " + op
                    + "; missing " + (latToken == null ? "'lat'" : "'lon'"), false);
            Pt point = new Pt();
            point.Lat = NumberField(latToken, "lat");
            point.Lon = NumberField(lonToken, "lon");
            CheckLatLon(point.Lat, point.Lon, "lat/lon");
            point.Source = "explicit lat/lon";
            return point;
        }

        /// <summary>`lat`+`lon`, or `unit` (the object's live position at request time). Both at once
        /// is refused: one of the two would silently win, and the caller would never learn which.</summary>
        private static Pt MarkerPoint(JObject req)
        {
            JToken unitToken = Present(req, "unit");
            JToken latToken = Present(req, "lat");
            JToken lonToken = Present(req, "lon");
            if (unitToken != null)
            {
                if (latToken != null || lonToken != null)
                    throw new ApiException(400, "bad_value",
                        "give either 'unit' or 'lat'/'lon', not both", false);
                return UnitPoint(WholeField(unitToken, "unit"), "unit");
            }
            return LatLonPoint(req, "kind 'marker'");
        }

        /// <summary>One ruler end, from any of the three spellings: `from:{lat,lon}`,
        /// `from_lat`+`from_lon`, or `from_unit`/`fromUnit`. Exactly one form is accepted; two would
        /// mean one silently wins and the caller never learns which.</summary>
        private static Pt RulerPoint(JObject req, string nestedField, string latField, string lonField,
            string unitField, string unitAlias)
        {
            JToken nestedToken = Present(req, nestedField);
            JToken latToken = Present(req, latField);
            JToken lonToken = Present(req, lonField);
            string unitName;
            JToken unitToken = AliasOf(req, new string[] { unitField, unitAlias }, out unitName);

            int forms = 0;
            if (nestedToken != null) forms++;
            if (latToken != null || lonToken != null) forms++;
            if (unitToken != null) forms++;
            if (forms == 0)
                throw new ApiException(400, "missing_field", "'" + nestedField + "', '"
                    + latField + "'/'" + lonField + "' or '" + unitField + "' is required for kind"
                    + " 'ruler'", false);
            if (forms > 1)
                throw new ApiException(400, "bad_value", "give one form of the ruler end '"
                    + nestedField + "': either '" + nestedField + "', or '" + latField + "'/'"
                    + lonField + "', or '" + unitField + "' - not " + forms + " of them at once", false);

            if (unitToken != null) return UnitPoint(WholeField(unitToken, unitName), unitName);
            if (nestedToken != null) return ParsePoint(nestedToken, nestedField);

            if (latToken == null || lonToken == null)
                throw new ApiException(400, "missing_field", "'" + latField + "' and '" + lonField
                    + "' must be given together; missing "
                    + (latToken == null ? "'" + latField + "'" : "'" + lonField + "'"), false);
            Pt point = new Pt();
            point.Lat = NumberField(latToken, latField);
            point.Lon = NumberField(lonToken, lonField);
            CheckLatLon(point.Lat, point.Lon, latField);
            point.Source = "explicit " + latField + "/" + lonField;
            return point;
        }

        /// <summary>`center:{lat,lon}` or the flattened `center_lat`/`center_lon`.</summary>
        private static Pt RingCenter(JObject req)
        {
            JToken nestedToken = Present(req, "center");
            JToken latToken = Present(req, "center_lat");
            JToken lonToken = Present(req, "center_lon");
            if (nestedToken != null && (latToken != null || lonToken != null))
                throw new ApiException(400, "bad_value",
                    "give either 'center' or 'center_lat'/'center_lon', not both", false);
            if (nestedToken != null) return ParsePoint(nestedToken, "center");
            if (latToken == null || lonToken == null)
                throw new ApiException(400, "missing_field", "'center' (or 'center_lat'/'center_lon')"
                    + " is required for kind 'ring'; missing "
                    + (latToken == null ? "'center_lat'" : "'center_lon'"), false);
            Pt point = new Pt();
            point.Lat = NumberField(latToken, "center_lat");
            point.Lon = NumberField(lonToken, "center_lon");
            CheckLatLon(point.Lat, point.Lon, "center");
            point.Source = "explicit center_lat/center_lon";
            return point;
        }

        private static Pt UnitPoint(int unitId, string field)
        {
            // REVIEW (round 2, blocker 3): the anchor resolved with GameReflect.UnitById and read
            // _geoPosition, so drawing a marker "at unit N" for a hull the player never detected both
            // used its true coordinate and echoed it back in geometry.point, where it then persisted
            // in /plot/drawings. A drawing anchor is a position read like any other.
            SeaPower.ObjectBase unit = Api.FindKnownUnit(unitId);
            // ObjectBase holds its position in a field rather than behind a property; MapPlot.cs
            // reads the same member for the same reason.
            object geo = GameReflect.Member(unit, "_geoPosition");
            if (geo == null) geo = GameReflect.Member(unit, "GeoPosition");
            double lat = GameReflect.Lat(geo);
            double lon = GameReflect.Lon(geo);
            if (double.IsNaN(lat) || double.IsNaN(lon))
                throw new ApiException(409, "unit_position_unreadable", "UniqueID " + unitId
                    + " has no readable Latitude/Longitude to draw at", false);
            CheckLatLon(lat, lon, "UniqueID " + unitId + " position");
            Pt point = new Pt();
            point.Lat = lat;
            point.Lon = lon;
            point.Source = "UniqueID " + unitId + " position at request time";
            return point;
        }

        // ================================================================ generic helpers

        private static Assign PointAssign(string memberName, Pt point, bool required)
        {
            Assign assign = new Assign();
            assign.Names = new string[] { memberName };
            assign.Fill = FillFromPoint;
            assign.Point = point;
            assign.Required = required;
            assign.What = memberName;
            return assign;
        }

        private static Assign AnchorAssign(string memberName, Pt point, bool required)
        {
            Assign assign = new Assign();
            assign.Names = new string[] { memberName };
            assign.Fill = FillFromAnchor;
            assign.Point = point;
            assign.Required = required;
            assign.What = memberName;
            return assign;
        }

        private static string SetFirst(object holder, string[] names, object value)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (GameReflect.SetMember(holder, names[i], value)) return names[i];
            }
            return null;
        }

        /// <summary>Invokes the parameterless Update() and returns null on success, or the failure.
        /// GameReflect.Call cannot be used here: it swallows exceptions and returns null for a void
        /// method, so "it threw" and "it worked" would look identical.</summary>
        private static string InvokeUpdate(object drawing)
        {
            Type type = drawing.GetType();
            Type[] noArguments = new Type[0];
            while (type != null)
            {
                MethodInfo method = type.GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance, null, noArguments, null);
                if (method != null)
                {
                    try
                    {
                        method.Invoke(drawing, null);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return Describe(ex);
                    }
                }
                type = type.BaseType;
            }
            return "no parameterless Update() on " + drawing.GetType().FullName;
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return "unknown error";
            Exception inner = ex;
            if (inner is TargetInvocationException && inner.InnerException != null)
                inner = inner.InnerException;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static JObject PtToken(Pt point)
        {
            JObject o = new JObject();
            o["lat"] = point.Lat;
            o["lon"] = point.Lon;
            return o;
        }

        private static JObject PtTokenWithSource(Pt point)
        {
            JObject o = PtToken(point);
            if (point.Source != null) o["source"] = point.Source;
            return o;
        }

        private static string ShortName(string typeName)
        {
            if (typeName == null) return null;
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        private static string ShortNameOfObject(object value)
        {
            if (value == null) return "null";
            return value.GetType().Name;
        }

        private static void MergeInto(JObject target, JObject source)
        {
            if (source == null) return;
            foreach (JProperty property in source.Properties()) target[property.Name] = property.Value;
        }

        private static JArray ToArray(List<string> items)
        {
            JArray array = new JArray();
            for (int i = 0; i < items.Count; i++) array.Add(items[i]);
            return array;
        }

        private static string[] Union(params string[][] groups)
        {
            List<string> all = new List<string>();
            for (int g = 0; g < groups.Length; g++)
            {
                for (int i = 0; i < groups[g].Length; i++)
                {
                    if (all.IndexOf(groups[g][i]) < 0) all.Add(groups[g][i]);
                }
            }
            return all.ToArray();
        }

        private static Dictionary<string, string[]> BuildKindFields()
        {
            Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            map["marker"] = Union(CommonFields, MarkerFields);
            map["ruler"] = Union(CommonFields, RulerFields);
            map["angle"] = Union(CommonFields, AngleFields);
            map["ring"] = Union(CommonFields, RingFields);
            map["polygon"] = Union(CommonFields, PolygonFields);
            map["text"] = Union(CommonFields, TextFields);
            return map;
        }

        private static string[] FieldsForKind(string kind)
        {
            string[] fields;
            if (KindFields.TryGetValue(kind, out fields)) return fields;
            return AllDrawFields;
        }
    }
}
