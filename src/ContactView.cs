// ContactView.cs — 把"玩家能观测到的接触"投影成 JSON。
//
// 为什么需要这个文件
// ------------------
// 现有 /units 与 /units/{id} 走的是 UnitJson(ObjectBase)：只要 IntelScope 认为玩家
// 有 fresh/live 接触，就把 ObjectBase 的真实 Position/Heading/Velocity/Altitude/
// 名称/型号/国籍 全部吐出去。那是一条外部目标情报泄漏路径（见
// designchange/11_1_landunits/OPERATION_COVERAGE_REVIEW_CODEX.md §9.1）。
// 本文件提供"只读玩家方航迹"的投影：入参是 SeaPower.Vehicle（也就是一张绘图桌上
// 玩家方的接触/航迹），出参只包含游戏自己已经算给玩家的估计量。
//
// 做什么
// ------
//   From(v, trackIdField)  单条接触 -> JObject
//   List(sourceTracks)     已有航迹列表 -> 逐条按 trackId 找到 Vehicle 再投影
//   Decorate(tracks)       就地给每条 track 补观测字段（给 Api.Tracks 之后接）
//
// 不做什么（硬边界，违反即等于重新打开泄漏）
// ------------------------------------------
// * 绝不读 ObjectBase.Position、Object.transform.position、Vehicle.RealUnityPosition；
// * 绝不读 TrackComponent.TrueTargetPosition；
// * 绝不因为"字段空着不好看"而补任何真值或默认值 —— 空着就是空着，另附 notes；
// * PositionEstimate 为空就给 positionSource:"unknown" + lat/lon:null，不猜；
// * 只有方位（FanInfo）时只给方位角与扇区半宽，不给坐标、不给任何距离；
// * 候选型号来自 ClassInformation 位集合 × UnitMetadataProvider.UnitIdMap，它是
//   一个"集合"不是概率分布，所以不输出、也不均分任何百分比；
// * confidence 直接是 VehicleComponent.Confidence 的原值，不改名、不换算、不用
//   Suspicion 顶替。
//
// 刻意回避的坑（都有实测记录）
// ----------------------------
// * 不调 Epoch.ToLive()：它背后是 EpochClock.CurrentGen，research/PROBE_FINDINGS.md
//   附录 B.4 记录反射读该成员会卡主线程 20s+。这里只用 Vehicle 上已经 live 化的
//   float 缓存（InitialDetectionEpoch / _detTrackEpochLive / _detTrackMaxAge）以及
//   GameTime.missionSessionTime，并把这一点写进 notes，让调用方能看见口径。
// * 不枚举 Unity.Entities.World.All（同一张表：死锁 20s）。只读
//   World.DefaultGameObjectInjectionWorld 这个静态属性（auto-property，纯字段读）。
// * 不读 SeapowerUI 里的 ErrorEllipseViewModel / BearingFanViewModel：那两个类型在
//   Managed 下没有单独可引用的程序集入口，且是 Noesis 视图对象（视图线程口径未验证）。
//   §9.2 要求的误差与方位都从 ECS 组件/Vehicle 原生数据取，不绕 UI。
//
// 没接线的部分（本文件不假装做了）
// --------------------------------
// * 速度/航向：Vehicle.Velocity / UnityVelocityVector 的赋值来源与 UI 显示条件都还没
//   追溯清楚，§9.2 明确"不能因它位于 Vehicle 就认定是安全估计值"，所以 speedKts /
//   headingDeg 恒为 null + motionNote，等来源口径确认后再接。
// * 高度：Vehicle.Altitude 是 Nullable<SourcedProperty<PointEstimate>>，本轮没追溯，
//   不输出（也就不可能误当真值）。
// * DetectedSignature 的位匹配（SignaturesViewModel 那套 Combine/ILookup）没有重做：
//   候选只按 ClassInformation 位集合给出，不做"检测特征 vs 候选特征"的配对打分。
// * Unity 坐标 -> 经纬度：游戏里没有 public 的 localV3ToLongLat，MapUnits.InverseWorld
//   是桥接私有的，ContactView 调不到。这里不需要它 —— 经纬度直接从
//   PositionEstimate.Item1（GeoPosition）取；KnownUnityPosition 只作为附带审计字段输出。
//
// C#5：不用字符串插值、?.、nameof、表达式体成员、out var。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using ClassInfoUtils = SeaPower.ClassInfoUtils;
using ClassInformation = SeaPower.ClassInformation;
using ComponentType = Unity.Entities.ComponentType;
using EntityManager = Unity.Entities.EntityManager;
using GeoPosition = SeaPower.GeoPosition;
using ObjectBase = SeaPower.ObjectBase;
using PositionEigpairs = SeaPower.PositionEigpairs;
using SimpleObjectParameters = SeaPower.SimpleObjectParameters;
using UnitMetadataProvider = SeaPower.UnitMetadataProvider;
using Vehicle = SeaPower.Vehicle;
using VehicleComponent = SeaPower.VehicleComponent;
using EcsWorld = Unity.Entities.World;

namespace SeaPowerAgentBridge
{
    internal static class ContactView
    {
        // 与 TrackScope 保持同一套错因字符串，方便 /tracks 的调用方统一处理。
        private const string OffThreadMessage = "contact projection must run on the game's main thread";

        // 原生 SignaturesViewModel 用 MAX_CLASSES_SHOWN 截断候选；那个常量的值没读到，
        // 这里给一个自己的上限并如实上报 truncated，避免把"我们截了"说成"游戏只有这么多"。
        private const int MaxCandidates = 32;
        private const int MaxNotes = 48;

        private const string CanaryText =
            "No ObjectBase.Position, Object.transform.position, Vehicle.RealUnityPosition or " +
            "TrackComponent.TrueTargetPosition is read by ContactView. Every value below comes from " +
            "the player-side plotting table (SeaPower.Vehicle) or from a component of that contact.";

        private const string ErrorUnitsNote =
            "PositionEigpairs is copied raw (MajorAxis/MajorStdDev/MinorAxis/MinorStdDev). " +
            "Units, axis convention and the UI display multiplier are NOT calibrated: do not label " +
            "these numbers as meters, as a 95% region, or as a confidence.";

        private const string ConfidenceNote =
            "Raw SeaPower.VehicleComponent.Confidence. BaseVehicleViewModel.UpdateFrom assigns it to the " +
            "UI Confidence property unchanged, so the display scale is uncalibrated. It is NOT 'the " +
            "probability this is the right model' and Suspicion is a different field.";

        private const string MotionNote =
            "speedKts/headingDeg are null on purpose: no velocity field of the contact has a confirmed " +
            "observation-only provenance yet, and §9.2 forbids filling them from the real object state.";

        private const string BearingNote =
            "Bearing-only contact: TryGetLatestBearingTrack returned a fan. The fan origin is withheld on " +
            "purpose (it can be another platform's own position) and FanInfo.RangeMeters is the fan's " +
            "extent, not the target range, so no distance is reported at all.";

        // ------------------------------------------------------------------
        // 入口
        // ------------------------------------------------------------------

        /// <summary>单条接触投影。trackIdField 是输出里承载航迹号的字段名（默认 "trackId"）。</summary>
        internal static JObject From(Vehicle v, string trackIdField)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);
            if (v == null) throw new ApiException(400, "bad_request", "ContactView.From needs a non-null SeaPower.Vehicle", false);
            List<string> notes = new List<string>();
            JObject o = Build(v, trackIdField, notes);
            AttachFooter(o, notes);
            return o;
        }

        /// <summary>批量：输入一串已有航迹（每条形如 {"trackId":1234,...}），逐条按 trackId 找到 Vehicle 后投影。</summary>
        internal static JObject List(JArray sourceTracks)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);
            return List(sourceTracks, null);
        }

        internal static JObject List(JArray sourceTracks, List<string> notes)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);
            if (sourceTracks == null) throw new ApiException(400, "bad_request", "ContactView.List needs a JArray of tracks", false);
            List<string> local = notes == null ? new List<string>() : notes;

            Dictionary<int, Vehicle> index = IndexVehicles(local);

            JArray outTracks = new JArray();
            JObject sourceCounts = new JObject();
            int resolved = 0;
            int unresolved = 0;

            for (int i = 0; i < sourceTracks.Count; i++)
            {
                JObject src = sourceTracks[i] as JObject;
                if (src == null)
                {
                    unresolved++;
                    AddCount(sourceCounts, "unresolved");
                    continue;
                }
                int id = ReadTrackId(src);
                Vehicle v = null;
                if (id > 0) index.TryGetValue(id, out v);
                if (v == null)
                {
                    unresolved++;
                    AddCount(sourceCounts, "unresolved");
                    JObject miss = (JObject)src.DeepClone();
                    MarkUnresolved(miss, id);
                    outTracks.Add(miss);
                    continue;
                }

                List<string> itemNotes = new List<string>();
                JObject proj = Build(v, "trackId", itemNotes);
                JObject merged = MergeTrack(src, proj);
                AttachFooter(merged, itemNotes);
                outTracks.Add(merged);
                resolved++;
                AddCount(sourceCounts, Str(merged["positionSource"]));
            }

            JObject res = new JObject();
            res["scope"] = "contact_projection";
            res["count"] = new JValue(sourceTracks.Count);
            res["resolved"] = new JValue(resolved);
            res["unresolved"] = new JValue(unresolved);
            res["positionSourceCounts"] = sourceCounts;
            res["tracks"] = outTracks;
            res["tablePreference"] = new JValue("player_taskforce_first");
            res["canary"] = new JValue(CanaryText);
            res["notes"] = NotesArray(local);
            return res;
        }

        /// <summary>就地给每条 track 补观测字段；补不进去的明确标 unknown 并留 note。</summary>
        internal static JArray Decorate(JArray tracks)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);
            return Decorate(tracks, null);
        }

        internal static JArray Decorate(JArray tracks, List<string> notes)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);
            if (tracks == null) throw new ApiException(400, "bad_request", "ContactView.Decorate needs a JArray of tracks", false);
            List<string> local = notes == null ? new List<string>() : notes;

            Dictionary<int, Vehicle> index = IndexVehicles(local);
            for (int i = 0; i < tracks.Count; i++)
            {
                JObject track = tracks[i] as JObject;
                if (track == null) continue;
                int id = ReadTrackId(track);
                Vehicle v = null;
                if (id > 0) index.TryGetValue(id, out v);
                if (v == null)
                {
                    MarkUnresolved(track, id);
                    continue;
                }
                List<string> itemNotes = new List<string>();
                JObject proj = Build(v, "trackId", itemNotes);
                MergeTrackInPlace(track, proj);
                AttachFooter(track, itemNotes);
            }
            return tracks;
        }

        // ------------------------------------------------------------------
        // 投影主体
        // ------------------------------------------------------------------

        private static JObject Build(Vehicle v, string trackIdField, List<string> notes)
        {
            JObject o = new JObject();
            string field = (trackIdField == null || trackIdField.Length == 0) ? "trackId" : trackIdField;

            int id = 0;
            try { id = v.Id; }
            catch (Exception ex) { Note(notes, "read_failed:Id:" + ex.GetType().Name); }
            if (id > 0)
            {
                o[field] = new JValue(id);
                if (!string.Equals(field, "trackId", StringComparison.Ordinal)) o["trackId"] = new JValue(id);
            }
            else
            {
                o[field] = JValue.CreateNull();
                if (!string.Equals(field, "trackId", StringComparison.Ordinal)) o["trackId"] = JValue.CreateNull();
                Note(notes, "track_id_unavailable: Vehicle.Id was " + id);
            }

            // ---- 观测时间 / 新鲜度（全部是 Vehicle 自己的缓存，不碰 Epoch.ToLive） ----
            double nowSec = StaticNum("SeaPower.GameTime", "missionSessionTime", notes, "missionSessionTime");
            double initialEpoch = Num(Member(v, "InitialDetectionEpoch"), notes, "InitialDetectionEpoch");
            double trackEpochLive = Num(Member(v, "_detTrackEpochLive"), notes, "_detTrackEpochLive");
            double trackMaxAge = Num(Member(v, "_detTrackMaxAge"), notes, "_detTrackMaxAge");
            bool? detHasComponent = Bool(Member(v, "_detTrackHasComponent"), notes, "_detTrackHasComponent");
            bool? fresh = Bool(Member(v, "IsDetectedTrackFresh"), notes, "IsDetectedTrackFresh");
            bool? selfCached = Bool(Member(v, "_isSelfCached"), notes, "_isSelfCached");
            bool? live = Bool(Member(v, "HasLiveTrack"), notes, "HasLiveTrack");

            o["missionSeconds"] = NumToken(nowSec);
            o["initialDetectionEpoch"] = NumToken(initialEpoch);
            o["firstDetectedAgeSec"] = AgeSec(nowSec, initialEpoch, initialEpoch > 0);
            o["detectedTrackHasComponent"] = BoolToken(detHasComponent);
            o["detectedTrackEpochLive"] = NumToken(trackEpochLive);
            o["detectedTrackMaxAgeSec"] = NumToken(trackMaxAge);
            o["lastDetectedAgeSec"] = AgeSec(nowSec, trackEpochLive, detHasComponent == true);
            o["fresh"] = BoolToken(fresh);
            o["selfCached"] = BoolToken(selfCached);
            o["live"] = BoolToken(live);
            o["timeBasis"] = new JValue(
                "ages are SeaPower.GameTime.missionSessionTime minus the Vehicle's already-live float " +
                "caches; SeaPower.Epoch.ToLive() is deliberately not called (EpochClock.CurrentGen is a " +
                "known main-thread trap). Live-normalisation of the two caches is assumed, not verified.");

            // ---- 位置 ----
            PositionRead pos = ReadPosition(v, fresh, selfCached, notes);
            o["positionSource"] = new JValue(pos.Source);
            o["lat"] = NumToken(pos.Lat);
            o["lon"] = NumToken(pos.Lon);
            if (pos.Source == "detected" || pos.Source == "extrapolated")
            {
                if (!IsNum(pos.Lat) || !IsNum(pos.Lon))
                {
                    Note(notes, "position_estimate_present_but_geo_invalid: PositionEstimate had a value but " +
                                "Latitude/Longitude were not finite; lat/lon left null on purpose.");
                }
            }
            if (pos.StaleDetectedPresent && (pos.Source == "unknown" || pos.Source == "bearing_only"))
            {
                Note(notes, "stale_detected_withheld: Vehicle.DetectedPosition has a value but the track is not " +
                            "fresh and the vehicle is not self-cached, so the native PositionEstimate returns " +
                            "ExtrapolatedPosition (null here). A stale detected fix is not substituted for it.");
            }
            if (!pos.NativeEstimatePresent && (pos.Source == "detected" || pos.Source == "extrapolated"))
            {
                Note(notes, "position_estimate_missing_but_component_used: a detected/extrapolated component had a " +
                            "value while Vehicle.PositionEstimate reported null; positionSource follows the native " +
                            "getter's IL priority order, please re-check against the running game.");
            }

            o["positionError"] = pos.HasEig ? ErrorToken(pos) : JValue.CreateNull();
            o["positionErrorNote"] = new JValue(ErrorUnitsNote);

            // ---- KnownUnityPosition（观测派生的 Unity 坐标，审计用；不是真值位置） ----
            o["knownUnity"] = KnownUnityToken(v, notes);

            // ---- 方位（仅在完全没有位置估计时探测） ----
            if (pos.Source == "unknown")
            {
                JObject fan = TryBearing(v, notes);
                if (fan != null)
                {
                    o["positionSource"] = new JValue("bearing_only");
                    o["bearing"] = fan;
                    o["bearingNote"] = new JValue(BearingNote);
                }
            }

            // ---- 接触组件（confidence / 分类 / 候选） ----
            VehicleComponent vc;
            bool hasVc = TryVehicleComponent(v, notes, out vc);
            o["vehicleComponentAvailable"] = new JValue(hasVc);
            if (hasVc)
            {
                o["confidence"] = NumToken(vc.Confidence);
                o["confidenceNote"] = new JValue(ConfidenceNote);
                o["detectionKind"] = new JValue(EnumName(vc.Kind));
                o["componentSuspicion"] = NumToken(vc.Suspicion);
            }
            else
            {
                o["confidence"] = JValue.CreateNull();
                o["confidenceNote"] = new JValue("VehicleComponent was not readable for this contact, so the " +
                                                "native Confidence is unavailable. Suspicion is NOT substituted.");
                o["detectionKind"] = JValue.CreateNull();
            }

            o["suspicion"] = NumToken(Num(Member(v, "Suspicion"), notes, "Suspicion"));
            o["suspicionNote"] = new JValue("Vehicle.Suspicion, a separate native scalar. Never used as confidence.");

            ClassInformation ci = default(ClassInformation);
            bool hasCi = hasVc && TryClassInformation(vc, notes, out ci);
            if (hasCi)
            {
                JObject ciJson = new JObject();
                ciJson["available"] = new JValue(true);
                ciJson["overrideIdentification"] = new JValue(ci.overrideIdentification);
                int numBits = CountBits(ci, notes);
                ciJson["numBits"] = new JValue(numBits);
                o["classInformation"] = ciJson;

                if (ci.overrideIdentification && numBits == 0)
                {
                    // 原生 SignaturesViewModel.UpdateFrom 在这个分支会：
                    //   IdForUnit(VehicleComponent.Object) -> ClassInformation.ForceID(id)
                    // 也就是拿真实 ObjectBase 去查型号再强制识别。这条路径读的是真值，
                    // 显式挡掉；候选保持为空，并留下 note 让调用方知道"这里被我们拦了"。
                    Note(notes, "override_identification_suppressed: native SignaturesViewModel would call " +
                                "UnitMetadataProvider.IdForUnit(VehicleComponent.Object) and ForceID() with the real " +
                                "object's id when overrideIdentification is set and the bit set is empty. That path " +
                                "reads the truth object; ContactView does not reproduce it, so candidates stay empty.");
                    o["overrideIdentificationSuppressed"] = new JValue(true);
                }

                int emitted = 0;
                bool truncated = false;
                string known = "unknown";
                JArray candidates = ReadCandidates(ci, notes, out emitted, out truncated, out known);
                o["candidates"] = candidates;
                o["candidateCount"] = new JValue(emitted);
                o["candidatesTruncated"] = new JValue(truncated);
                o["candidatesNote"] = new JValue(
                    "Candidates are the set of classes the contact's ClassInformation bits select in " +
                    "UnitMetadataProvider.UnitIdMap. It is a set, not a probability distribution: no weights, " +
                    "no percentages, no ranking is implied by the order.");
                o["knownClass"] = new JValue(known);
                o["knownClassBasis"] = new JValue(
                    "derived only from the observed ClassInformation bits (UnitIdMap[bit]._objectType); the real " +
                    "object type of the target is never consulted");
                if (truncated) Note(notes, "candidates_truncated: more than " + MaxCandidates +
                                           " classes in the bit set; the native UI has its own MAX_CLASSES_SHOWN limit.");
                if (string.Equals(known, "unknown", StringComparison.Ordinal) && emitted > 0)
                {
                    Note(notes, "known_class_mixed_or_unmapped: candidate object types did not collapse to a single " +
                                "category, so knownClass is unknown instead of a guess.");
                }
            }
            else
            {
                JObject ciJson = new JObject();
                ciJson["available"] = new JValue(false);
                o["classInformation"] = ciJson;
                o["candidates"] = new JArray();
                o["candidateCount"] = new JValue(0);
                o["candidatesTruncated"] = new JValue(false);
                o["candidatesNote"] = new JValue("ClassInformation was not readable, so no candidates are offered. " +
                                                 "Empty here means 'not connected', not 'none match'.");
                o["knownClass"] = new JValue("unknown");
                o["knownClassBasis"] = new JValue(
                    "unavailable: ClassInformation of the contact's CurrentState could not be read, so no observed " +
                    "class basis exists. Contact kind stays unknown rather than being taken from the real object.");
            }

            // ---- 观测面其它量 ----
            o["updateEpoch"] = NumToken(Num(Member(v, "UpdateEpoch"), notes, "UpdateEpoch"));
            o["visual"] = BoolToken(Bool(Member(v, "IsObservedByVisual"), notes, "IsObservedByVisual"));
            o["classified"] = BoolToken(Bool(Member(v, "IsClassified"), notes, "IsClassified"));
            o["identified"] = new JValue(Truth(Api.Truthy(Member(v, "Identified"))));
            o["identifiedNote"] = new JValue(
                "Vehicle.Identified (UniRx ReactiveProperty<bool>) as the game itself exposes it on the track. The " +
                "assignment site was not traced this round, so this is the track's own flag, not a re-derivation.");

            string observedClass = null;
            try { observedClass = Api.NameField(Member(v, "Class")); }
            catch (Exception ex) { Note(notes, "read_failed:Class:" + ex.GetType().Name); }
            o["observedClass"] = observedClass == null ? JValue.CreateNull() : new JValue(observedClass);
            o["observedClassNote"] = new JValue(
                "Vehicle.Class maps to the class names the game derives from the contact's ClassInformation bits " +
                "(SourcedProperty<string>). It is reported as text only; knownClass is the mapped category above.");

            // ---- 明确留空 / 未接线的量 ----
            o["speedKts"] = JValue.CreateNull();
            o["headingDeg"] = JValue.CreateNull();
            o["motionNote"] = new JValue(MotionNote);
            o["altitudeMeters"] = JValue.CreateNull();
            o["altitudeNote"] = new JValue(
                "Vehicle.Altitude is Nullable<SourcedProperty<PointEstimate>> and its provenance was not traced this " +
                "round, so no altitude is reported rather than reporting a possibly-truth value.");

            return o;
        }

        // ------------------------------------------------------------------
        // 位置
        // ------------------------------------------------------------------

        private sealed class PositionRead
        {
            public string Source = "unknown";
            public double Lat = double.NaN;
            public double Lon = double.NaN;
            public bool HasEig = false;
            public double MajorX = double.NaN;
            public double MajorY = double.NaN;
            public double MajorStdDev = double.NaN;
            public double MinorX = double.NaN;
            public double MinorY = double.NaN;
            public double MinorStdDev = double.NaN;
            public bool StaleDetectedPresent = false;
            public bool NativeEstimatePresent = false;
        }

        private static PositionRead ReadPosition(Vehicle v, bool? fresh, bool? selfCached, List<string> notes)
        {
            PositionRead r = new PositionRead();
            try
            {
                var dp = v.DetectedPosition;
                var xp = v.ExtrapolatedPosition;
                var pe = v.PositionEstimate;
                r.NativeEstimatePresent = pe.HasValue;

                bool detectedUsable = dp.HasValue && (fresh == true || selfCached == true);
                if (detectedUsable)
                {
                    r.Source = "detected";
                    FillGeo(r, dp.Value.Item1);
                    FillEig(r, dp.Value.Item2);
                }
                else if (xp.HasValue)
                {
                    // IL: DetectedPosition 不可用时 PositionEstimate 直接返回 ExtrapolatedPosition
                    r.Source = "extrapolated";
                    FillGeo(r, xp.Value.Item1);
                    FillEig(r, xp.Value.Item2);
                }
                else if (dp.HasValue)
                {
                    r.StaleDetectedPresent = true;
                    if ((fresh == null || selfCached == null) && pe.HasValue && SameGeo(pe.Value.Item1, dp.Value.Item1))
                    {
                        // fresh/_isSelfCached 没读到，但原生的 PositionEstimate 指的就是这条 detected fix，
                        // 说明游戏自己认为它可用。跟着原生走，并把不确定性写进 note。
                        r.Source = "detected";
                        FillGeo(r, dp.Value.Item1);
                        FillEig(r, dp.Value.Item2);
                        Note(notes, "fresh_flag_unreadable_matched_native_estimate: IsDetectedTrackFresh/_isSelfCached " +
                                    "could not be read, so the detected fix is used only because Vehicle.PositionEstimate " +
                                    "itself points at it.");
                    }
                }

                bool peSays = pe.HasValue;
                bool weSay = (r.Source == "detected" || r.Source == "extrapolated");
                if (peSays != weSay)
                {
                    Note(notes, "position_estimate_mismatch: Vehicle.PositionEstimate.HasValue=" + peSays.ToString() +
                                " but the IL-priority reconstruction produced source=" + r.Source +
                                ". Please re-check against the running game.");
                }
            }
            catch (Exception ex)
            {
                Note(notes, "position_read_failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return r;
        }

        private static void FillGeo(PositionRead r, GeoPosition g)
        {
            r.Lat = g.Latitude;
            r.Lon = g.Longitude;
        }

        private static void FillEig(PositionRead r, PositionEigpairs e)
        {
            r.HasEig = true;
            r.MajorX = e.MajorAxis.x;
            r.MajorY = e.MajorAxis.y;
            r.MajorStdDev = e.MajorStdDev;
            r.MinorX = e.MinorAxis.x;
            r.MinorY = e.MinorAxis.y;
            r.MinorStdDev = e.MinorStdDev;
        }

        private static bool SameGeo(GeoPosition a, GeoPosition b)
        {
            double alat = a.Latitude;
            double alon = a.Longitude;
            double blat = b.Latitude;
            double blon = b.Longitude;
            if (double.IsNaN(alat) || double.IsNaN(blat)) return false;
            return Math.Abs(alat - blat) < 1e-9 && Math.Abs(alon - blon) < 1e-9;
        }

        private static JToken ErrorToken(PositionRead r)
        {
            JObject e = new JObject();
            JObject major = new JObject();
            major["x"] = NumToken(r.MajorX);
            major["y"] = NumToken(r.MajorY);
            JObject minor = new JObject();
            minor["x"] = NumToken(r.MinorX);
            minor["y"] = NumToken(r.MinorY);
            e["majorAxis"] = major;
            e["majorStdDev"] = NumToken(r.MajorStdDev);
            e["minorAxis"] = minor;
            e["minorStdDev"] = NumToken(r.MinorStdDev);
            e["unitsCalibrated"] = new JValue(false);
            e["source"] = new JValue("SeaPower.DetectedPosition/EstimatedPosition.Eigpairs (PositionEigpairs)");
            e["note"] = new JValue(ErrorUnitsNote);
            return e;
        }

        private static JToken KnownUnityToken(Vehicle v, List<string> notes)
        {
            try
            {
                var kp = v.KnownUnityPosition;
                if (!kp.HasValue)
                {
                    return JValue.CreateNull();
                }
                JObject u = new JObject();
                u["x"] = NumToken(kp.Value.x);
                u["y"] = NumToken(kp.Value.y);
                u["z"] = NumToken(kp.Value.z);
                u["source"] = new JValue("Vehicle.KnownUnityPosition (PositionEstimate.Item1 -> Utils.longLatToLocalV3)");
                u["note"] = new JValue(
                    "Observation-derived Unity position, NOT Object.transform.position. Its numeric value depends on " +
                    "Globals._currentCenterTile, so do not persist it across map recentres. lat/lon above come " +
                    "straight from PositionEstimate.Item1; no Unity->latlon inverse is performed here because the " +
                    "game exposes none and MapUnits' inverse is private to that file.");
                return u;
            }
            catch (Exception ex)
            {
                Note(notes, "known_unity_position_failed: " + ex.GetType().Name + ": " + ex.Message);
                return JValue.CreateNull();
            }
        }

        // ------------------------------------------------------------------
        // 仅方位
        // ------------------------------------------------------------------

        private static JObject TryBearing(Vehicle v, List<string> notes)
        {
            Type vType = typeof(Vehicle);
            Type fanType = null;
            try { fanType = vType.GetNestedType("FanInfo", BindingFlags.Public | BindingFlags.NonPublic); }
            catch (Exception ex) { Note(notes, "bearing_faninfo_lookup_failed: " + ex.GetType().Name); }
            if (fanType == null)
            {
                Note(notes, "bearing_unavailable: nested type SeaPower.Vehicle/FanInfo not found by reflection.");
                return null;
            }

            MethodInfo mi = null;
            try
            {
                mi = vType.GetMethod("TryGetLatestBearingTrack",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex) { Note(notes, "bearing_method_lookup_failed: " + ex.GetType().Name); }
            if (mi == null)
            {
                Note(notes, "bearing_unavailable: Vehicle.TryGetLatestBearingTrack not found by reflection.");
                return null;
            }

            EntityManager em;
            if (!TryEntityManager(notes, out em))
            {
                Note(notes, "bearing_unavailable: no EntityManager, and TryGetLatestBearingTrack needs one.");
                return null;
            }

            object fan;
            try { fan = Activator.CreateInstance(fanType); }
            catch (Exception ex)
            {
                Note(notes, "bearing_faninfo_create_failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            object[] args = new object[2];
            args[0] = em;
            args[1] = fan;
            object ret;
            try { ret = mi.Invoke(v, args); }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException == null ? ex : ex.InnerException;
                Note(notes, "bearing_probe_failed: " + inner.GetType().Name + ": " + inner.Message);
                return null;
            }

            if (!(ret is bool) || !((bool)ret))
            {
                Note(notes, "bearing_unavailable: TryGetLatestBearingTrack returned " + (ret == null ? "null" : ret.ToString()) + ".");
                return null;
            }

            object fanOut = args[1];
            double bearing = Num(Member(fanOut, "BearingDeg"), notes, "FanInfo.BearingDeg");
            double half = Num(Member(fanOut, "HalfAngleDeg"), notes, "FanInfo.HalfAngleDeg");
            if (!IsNum(bearing))
            {
                Note(notes, "bearing_unavailable: fan returned true but BearingDeg was not readable.");
                return null;
            }

            JObject b = new JObject();
            b["bearingDeg"] = NumToken(bearing);
            b["halfAngleDeg"] = NumToken(half);
            b["hasLatLon"] = new JValue(false);
            b["hasRange"] = new JValue(false);
            b["source"] = new JValue("SeaPower.Vehicle.TryGetLatestBearingTrack(EntityManager, out FanInfo)");
            b["withheld"] = new JValue("fan Origin (may be a foreign platform's own position) and FanInfo.RangeMeters " +
                                       "(fan extent, not target range)");
            return b;
        }

        // ------------------------------------------------------------------
        // 接触组件：VehicleComponent / ClassInformation / 候选
        // ------------------------------------------------------------------

        private static bool TryEntityManager(List<string> notes, out EntityManager em)
        {
            em = default(EntityManager);
            EcsWorld world;
            try { world = EcsWorld.DefaultGameObjectInjectionWorld; }
            catch (Exception ex)
            {
                Note(notes, "entity_manager_world_read_failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            if (world == null)
            {
                Note(notes, "entity_manager_unavailable: World.DefaultGameObjectInjectionWorld is null (no ECS world yet).");
                return false;
            }
            try
            {
                em = world.EntityManager;
                return true;
            }
            catch (Exception ex)
            {
                Note(notes, "entity_manager_unavailable: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool TryVehicleComponent(Vehicle v, List<string> notes, out VehicleComponent vc)
        {
            vc = default(VehicleComponent);
            EntityManager em;
            if (!TryEntityManager(notes, out em)) return false;

            var entity = v.Entity;
            try
            {
                if (!em.Exists(entity))
                {
                    Note(notes, "vehicle_entity_missing: Vehicle.Entity is not in the ECS world (Entity.Null or a " +
                                "purely local plotting vehicle), so VehicleComponent is unavailable.");
                    return false;
                }
                if (!em.HasComponent(entity, ComponentType.ReadOnly(typeof(VehicleComponent))))
                {
                    Note(notes, "vehicle_component_missing: the contact entity has no VehicleComponent.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Note(notes, "vehicle_component_probe_failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            return TryReadComponent<VehicleComponent>(em, entity, typeof(VehicleComponent), "VehicleComponent", notes, out vc);
        }

        private static bool TryClassInformation(VehicleComponent vc, List<string> notes, out ClassInformation ci)
        {
            ci = default(ClassInformation);
            EntityManager em;
            if (!TryEntityManager(notes, out em)) return false;

            var state = vc.CurrentState;
            try
            {
                if (!em.Exists(state))
                {
                    Note(notes, "class_information_missing: VehicleComponent.CurrentState entity does not exist.");
                    return false;
                }
                if (!em.HasComponent(state, ComponentType.ReadOnly(typeof(ClassInformation))))
                {
                    Note(notes, "class_information_missing: the contact state has no ClassInformation component, so no " +
                                "observed class basis exists.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Note(notes, "class_information_probe_failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            return TryReadComponent<ClassInformation>(em, state, typeof(ClassInformation), "ClassInformation", notes, out ci);
        }

        // GetComponentData<T> 的这个重载带 csc 4.0（C#5 编译器）不认识的泛型约束，
        // 直接调用会 CS0570 "not supported by the language"。所以这里走一次带缓存的反射，
        // 只把 sealed 的 MethodInfo 缓存起来，失败一律记 note 不抛。
        private static readonly Dictionary<Type, MethodInfo> ComponentGetters = new Dictionary<Type, MethodInfo>();

        private static MethodInfo ComponentGetter(Type componentType)
        {
            MethodInfo cached = null;
            if (ComponentGetters.TryGetValue(componentType, out cached)) return cached;
            MethodInfo found = null;
            try
            {
                MethodInfo[] methods = typeof(EntityManager).GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (!m.IsGenericMethodDefinition) continue;
                    if (!string.Equals(m.Name, "GetComponentData", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType != typeof(Unity.Entities.Entity)) continue;
                    found = m.MakeGenericMethod(componentType);
                    break;
                }
            }
            catch (Exception)
            {
                found = null;
            }
            ComponentGetters[componentType] = found;
            return found;
        }

        private static bool TryReadComponent<T>(EntityManager em, Unity.Entities.Entity entity, Type componentType,
                                                string label, List<string> notes, out T value) where T : struct
        {
            value = default(T);
            MethodInfo mi = ComponentGetter(componentType);
            if (mi == null)
            {
                Note(notes, "component_getter_unavailable: " + label);
                return false;
            }
            object receiver = em;
            object[] args = new object[1];
            args[0] = entity;
            object ret;
            try { ret = mi.Invoke(receiver, args); }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException == null ? ex : ex.InnerException;
                Note(notes, "component_read_failed: " + label + ": " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
            if (ret == null)
            {
                Note(notes, "component_read_null: " + label);
                return false;
            }
            value = (T)ret;
            return true;
        }

        private static int CountBits(ClassInformation ci, List<string> notes)
        {
            try { return ClassInfoUtils.NumBits<ClassInformation>(ci); }
            catch (Exception ex)
            {
                Note(notes, "num_bits_failed: " + ex.GetType().Name + ": " + ex.Message);
                return -1;
            }
        }

        private static JArray ReadCandidates(ClassInformation ci, List<string> notes,
                                            out int emitted, out bool truncated, out string knownClass)
        {
            emitted = 0;
            truncated = false;
            knownClass = "unknown";
            JArray arr = new JArray();
            HashSet<string> categories = new HashSet<string>(StringComparer.Ordinal);

            Dictionary<int, SimpleObjectParameters> map = null;
            try
            {
                // 原生路径：Singleton<UnitMetadataProvider>.Instance.UnitIdMap
                // Instance 的 getter 可能会自动创建实例，所以先用 InstanceExists(false) 探一下，
                // 避免我们的只读投影顺手造出一个管理器对象。
                if (!UnitMetadataProvider.InstanceExists(false))
                {
                    Note(notes, "unit_metadata_unavailable: Singleton<UnitMetadataProvider>.Instance does not exist, so " +
                                "ClassInformation bits cannot be resolved to class names.");
                    return arr;
                }
                UnitMetadataProvider provider = UnitMetadataProvider.Instance;
                if (provider == null)
                {
                    Note(notes, "unit_metadata_unavailable: UnitMetadataProvider.Instance was null.");
                    return arr;
                }
                map = provider.UnitIdMap;
                if (map == null)
                {
                    Note(notes, "unit_metadata_unavailable: UnitMetadataProvider.UnitIdMap was null.");
                    return arr;
                }
            }
            catch (Exception ex)
            {
                Note(notes, "unit_metadata_read_failed: " + ex.GetType().Name + ": " + ex.Message);
                return arr;
            }

            int seen = 0;
            try
            {
                foreach (int bit in ClassInfoUtils.EachBit<ClassInformation>(ci))
                {
                    seen++;
                    if (emitted >= MaxCandidates)
                    {
                        truncated = true;
                        continue;
                    }
                    SimpleObjectParameters p;
                    if (!map.TryGetValue(bit, out p) || p == null)
                    {
                        Note(notes, "candidate_bit_unmapped: ClassInformation bit " + bit + " has no UnitIdMap entry.");
                        continue;
                    }
                    JObject c = new JObject();
                    c["classRef"] = p._objectIniName == null ? JValue.CreateNull() : new JValue(p._objectIniName);
                    string display = null;
                    try { display = p.ClassName; }
                    catch (Exception ex) { Note(notes, "candidate_class_name_failed: " + ex.GetType().Name); }
                    c["className"] = display == null ? JValue.CreateNull() : new JValue(display);
                    string objectType = EnumName(p._objectType);
                    c["objectType"] = objectType == null ? JValue.CreateNull() : new JValue(objectType);
                    c["weight"] = JValue.CreateNull();
                    c["weightNote"] = new JValue("no weight exists natively; the set is unordered");
                    arr.Add(c);
                    emitted++;
                    categories.Add(MapObjectType(objectType));
                }
            }
            catch (Exception ex)
            {
                Note(notes, "candidate_enumeration_failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            // 大类只能从观测到的候选类型收敛出来：只有一个大类才敢说，否则 unknown。
            categories.Remove("unknown");
            if (categories.Count == 1)
            {
                foreach (string only in categories) knownClass = only;
            }
            if (seen > emitted && !truncated)
            {
                Note(notes, "candidate_bits_skipped: " + (seen - emitted) + " bit(s) produced no candidate.");
            }
            return arr;
        }

        private static string MapObjectType(string objectType)
        {
            if (objectType == null) return "unknown";
            if (string.Equals(objectType, "Vessel", StringComparison.Ordinal)) return "surface_ship";
            if (string.Equals(objectType, "Submarine", StringComparison.Ordinal)) return "submarine";
            if (string.Equals(objectType, "Aircraft", StringComparison.Ordinal)) return "aircraft";
            if (string.Equals(objectType, "Helicopter", StringComparison.Ordinal)) return "aircraft";
            if (string.Equals(objectType, "LandUnit", StringComparison.Ordinal)) return "land_unit";
            return "unknown";
        }

        // ------------------------------------------------------------------
        // 航迹索引（只用玩家方/玩家可见的绘图桌）
        // ------------------------------------------------------------------

        private static Dictionary<int, Vehicle> IndexVehicles(List<string> notes)
        {
            Dictionary<int, Vehicle> index = new Dictionary<int, Vehicle>();

            // 先取玩家自己的任务编组绘图桌：§9.3 要求多表并存时优先原生融合结果，
            // 而玩家方这张表就是游戏自己的融合结果。
            try
            {
                SeaPower.Taskforce player = SeaPower.Globals._playerTaskforce;
                if (player == null)
                {
                    Note(notes, "player_taskforce_missing: Globals._playerTaskforce is null (no mission loaded?).");
                }
                else
                {
                    AddTable(index, player.PlottingTable, notes, "player");
                }
            }
            catch (Exception ex)
            {
                Note(notes, "player_table_failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            // 再补齐桥接本来就枚举得到的其它单位表（Api.AllObjects 已经是 /tracks 的口径），
            // 只补还没有的 id，玩家表优先。
            List<ObjectBase> units = null;
            try { units = Api.AllObjects(); }
            catch (Exception ex) { Note(notes, "all_objects_failed: " + ex.GetType().Name + ": " + ex.Message); }
            if (units != null)
            {
                int other = 0;
                for (int i = 0; i < units.Count; i++)
                {
                    ObjectBase o = units[i];
                    if (o == null) continue;
                    try
                    {
                        object pt = GameReflect.Member(o, "PlottingTable");
                        if (pt == null) continue;
                        other += AddTable(index, pt, notes, "other");
                    }
                    catch (Exception ex)
                    {
                        Note(notes, "table_walk_failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                if (other > 0)
                {
                    Note(notes, "track_index_secondary_tables: " + other + " track(s) resolved from tables other than " +
                                "the player taskforce table. Prefer the player-table copy when both exist.");
                }
            }
            return index;
        }

        private static int AddTable(Dictionary<int, Vehicle> index, object table, List<string> notes, string label)
        {
            if (table == null) return 0;
            object vehicles = null;
            try { vehicles = GameReflect.Member(table, "Vehicles"); }
            catch (Exception ex)
            {
                Note(notes, "table_vehicles_failed: " + label + ": " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
            IEnumerable en = vehicles as IEnumerable;
            if (en == null) return 0;

            int added = 0;
            try
            {
                foreach (object raw in en)
                {
                    Vehicle v = raw as Vehicle;
                    if (v == null) continue;
                    int id = 0;
                    try { id = v.Id; }
                    catch (Exception) { continue; }
                    if (id <= 0) continue;
                    if (index.ContainsKey(id)) continue;
                    index[id] = v;
                    added++;
                }
            }
            catch (Exception ex)
            {
                Note(notes, "table_enumeration_failed: " + label + ": " + ex.GetType().Name + ": " + ex.Message +
                            " (the plotting table mutates while the player runs; a partial index is used).");
            }
            return added;
        }

        // ------------------------------------------------------------------
        // 合并 / 标记
        // ------------------------------------------------------------------

        private static JObject MergeTrack(JObject source, JObject projection)
        {
            JObject merged = (JObject)source.DeepClone();
            MergeTrackInPlace(merged, projection);
            return merged;
        }

        private static void MergeTrackInPlace(JObject target, JObject projection)
        {
            foreach (KeyValuePair<string, JToken> kv in projection)
            {
                string key = kv.Key;
                // 航迹身份只由调用方/游戏决定，投影不改写已有 trackId。
                if (string.Equals(key, "trackId", StringComparison.Ordinal) && target[key] != null) continue;
                target[key] = kv.Value;
            }
        }

        private static void MarkUnresolved(JObject track, int id)
        {
            track["positionSource"] = new JValue("unknown");
            track["lat"] = JValue.CreateNull();
            track["lon"] = JValue.CreateNull();
            track["confidence"] = JValue.CreateNull();
            track["knownClass"] = new JValue("unknown");
            track["candidates"] = new JArray();
            track["contactNote"] = new JValue(
                "no player-side SeaPower.Vehicle could be resolved for trackId " + id +
                "; fields left null instead of being filled from the real object");
            JArray n = track["notes"] as JArray;
            if (n == null) { n = new JArray(); track["notes"] = n; }
            n.Add(new JValue("track_unresolved:trackId=" + id));
        }

        private static int ReadTrackId(JObject track)
        {
            JToken t = track["trackId"];
            if (t == null) return 0;
            try
            {
                if (t.Type == JTokenType.Integer) return (int)t;
                if (t.Type == JTokenType.Float) return (int)(double)t;
                int parsed;
                if (int.TryParse(Str(t), out parsed)) return parsed;
            }
            catch (Exception) { }
            return 0;
        }

        // ------------------------------------------------------------------
        // 小工具：读字段 / JSON 拼装
        // ------------------------------------------------------------------

        private static object Member(object holder, string name)
        {
            if (holder == null || name == null) return null;
            try { return GameReflect.Member(holder, name); }
            catch (Exception) { return null; }
        }

        private static double StaticNum(string typeName, string name, List<string> notes, string label)
        {
            object raw = null;
            try { raw = GameReflect.StaticMember(typeName, name); }
            catch (Exception ex)
            {
                Note(notes, "read_failed:" + label + ":" + ex.GetType().Name);
                return double.NaN;
            }
            if (raw == null)
            {
                Note(notes, "field_missing:" + typeName + "." + name);
                return double.NaN;
            }
            return Num(raw, notes, label);
        }

        private static double Num(object raw, List<string> notes, string label)
        {
            if (raw == null) return double.NaN;
            try
            {
                if (raw is double) return (double)raw;
                if (raw is float) return (double)(float)raw;
                if (raw is int) return (double)(int)raw;
                if (raw is long) return (double)(long)raw;
                if (raw is short) return (double)(short)raw;
                if (raw is byte) return (double)(byte)raw;
                if (raw is sbyte) return (double)(sbyte)raw;
                if (raw is uint) return (double)(uint)raw;
                if (raw is ulong) return (double)(ulong)raw;
                if (raw is decimal) return (double)(decimal)raw;
            }
            catch (Exception) { }
            Note(notes, "field_not_numeric:" + label + ":" + raw.GetType().Name);
            return double.NaN;
        }

        private static bool? Bool(object raw, List<string> notes, string label)
        {
            if (raw == null) return null;
            if (raw is bool) return (bool)raw;
            Note(notes, "field_not_bool:" + label + ":" + raw.GetType().Name);
            return null;
        }

        private static bool Truth(bool b) { return b; }

        private static string EnumName(object raw)
        {
            if (raw == null) return null;
            try
            {
                Type t = raw.GetType();
                if (t.IsEnum) return raw.ToString();
                return raw.ToString();
            }
            catch (Exception) { return null; }
        }

        private static string Str(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Null) return null;
            try { return t.ToString(); }
            catch (Exception) { return null; }
        }

        private static bool IsNum(double d)
        {
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }

        private static JToken NumToken(double d)
        {
            if (!IsNum(d)) return JValue.CreateNull();
            return new JValue(d);
        }

        private static JToken BoolToken(bool? b)
        {
            if (!b.HasValue) return JValue.CreateNull();
            return new JValue(b.Value);
        }

        private static JToken AgeSec(double now, double then, bool usable)
        {
            if (!usable || !IsNum(now) || !IsNum(then)) return JValue.CreateNull();
            double age = now - then;
            return NumToken(age);
        }

        private static void AddCount(JObject counts, string key)
        {
            if (key == null || key.Length == 0) key = "unknown";
            JToken cur = counts[key];
            int n = 0;
            if (cur != null)
            {
                try { n = (int)cur; }
                catch (Exception) { n = 0; }
            }
            counts[key] = new JValue(n + 1);
        }

        private static void Note(List<string> notes, string text)
        {
            if (notes == null || text == null) return;
            if (notes.Count >= MaxNotes) return;
            if (notes.Contains(text)) return;
            notes.Add(text);
        }

        private static JArray NotesArray(List<string> notes)
        {
            JArray arr = new JArray();
            if (notes == null) return arr;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < notes.Count && arr.Count < MaxNotes; i++)
            {
                string s = notes[i];
                if (s == null) continue;
                if (!seen.Add(s)) continue;
                arr.Add(new JValue(s));
            }
            return arr;
        }

        private static void AttachFooter(JObject target, List<string> notes)
        {
            target["canary"] = new JValue(CanaryText);
            target["notes"] = NotesArray(notes);
        }
    }
}
