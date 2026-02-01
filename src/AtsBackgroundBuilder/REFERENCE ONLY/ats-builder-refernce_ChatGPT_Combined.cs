// AUTO-GENERATED MERGE FILE FOR REVIEW
// Generated: 2026-01-31 22:45:39

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\Properties\AssemblyInfo.cs
/////////////////////////////////////////////////////////////////////

using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.Runtime;
using ResidenceSync.UI;
[assembly: CommandClass(typeof(ResidenceSync.ResidenceSyncCommands))]
[assembly: CommandClass(typeof(RSUiCommands))]

[assembly: AssemblyTitle("ResidenceSync")]
[assembly: AssemblyDescription("Residence synchronization tools for AutoCAD.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("ResidenceSync")]
[assembly: AssemblyProduct("ResidenceSync")]
[assembly: AssemblyCopyright("Copyright Â© ResidenceSync 2024")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("2d6e1d6c-7235-4fbb-8bb5-5a1f9deecb3a")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\ResidenceSyncCommands.cs
/////////////////////////////////////////////////////////////////////

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.Constants;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DbOpenMode = Autodesk.AutoCAD.DatabaseServices.OpenMode;
using OdDataType = Autodesk.Gis.Map.Constants.DataType;
using OdOpenMode = Autodesk.Gis.Map.Constants.OpenMode;
using OdTable = Autodesk.Gis.Map.ObjectData.Table;
using Autodesk.Gis.Map.Project; // for ProjectModel


namespace ResidenceSync
{
    public class ResidenceSyncCommands
    {
        private const string MASTER_FILES_DIRECTORY = @"M:\Drafting\_SHARED FILES\_CG_SHARED";
        private const string PRIMARY_INDEX_DIRECTORY = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
        private const string FALLBACK_INDEX_DIRECTORY = @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\RES MANAGER";

        private enum CoordinateZone
        {
            Zone11 = 11,
            Zone12 = 12
        }

        private static string FormatZoneNumber(CoordinateZone zone)
            => ((int)zone).ToString(CultureInfo.InvariantCulture);

        private static string GetMasterResidencesPath(CoordinateZone zone)
            => Path.Combine(MASTER_FILES_DIRECTORY, $"Master_Residences_Z{FormatZoneNumber(zone)}.dwg");

        private static string GetMasterSectionsPath(CoordinateZone zone)
            => Path.Combine(MASTER_FILES_DIRECTORY, $"Master_Sections_Z{FormatZoneNumber(zone)}.dwg");

        private static string GetSectionsIndexDirectory()
            => Directory.Exists(PRIMARY_INDEX_DIRECTORY) ? PRIMARY_INDEX_DIRECTORY : FALLBACK_INDEX_DIRECTORY;

        private static string GetMasterSectionsIndexJsonPath(CoordinateZone zone)
            => Path.Combine(GetSectionsIndexDirectory(), $"Master_Sections.index_Z{FormatZoneNumber(zone)}.jsonl");

        private static string GetMasterSectionsIndexCsvPath(CoordinateZone zone)
            => Path.Combine(GetSectionsIndexDirectory(), $"Master_Sections.index_Z{FormatZoneNumber(zone)}.csv");

        private static bool TryConvertToZone(int zoneNumber, out CoordinateZone zone)
        {
            switch (zoneNumber)
            {
                case 11:
                    zone = CoordinateZone.Zone11;
                    return true;
                case 12:
                    zone = CoordinateZone.Zone12;
                    return true;
                default:
                    zone = default;
                    return false;
            }
        }
        private const string PREFERRED_OD_TABLE = "SECTIONS";
        private const string RESIDENCE_LAYER = "Z-RESIDENCE";

        // Tolerances (metres, WCS)
        private const double DEDUPE_TOL = 0.25;  // merge if within 25 cm
        private const double REPLACE_TOL = 3.0;   // replace if within 3 m
        private const double ERASE_TOL = 0.001;  // polygon test epsilon (ray-cast denom guard)
        private const double TRANSFORM_VALIDATION_TOL = 0.75; // scaled push must align within < 1 m


        // ----- OD field alias sets (used for reading/writing across mixed tables) -----
        private static readonly string[] JOB_ALIASES = { "JOB_NUM", "JOBNUM", "JOB", "JOB_NUMBER" };
        private static readonly string[] DESC_ALIASES = { "DESCRIPTION", "DESC", "DESCRIP", "DESCR" };
        private static readonly string[] NOTES_ALIASES = { "NOTES", "NOTE", "COMMENTS", "COMMENT", "REMARKS" };



        // Recognized residence block names (case-insensitive)
        private static readonly HashSet<string> RES_BLOCK_NAMES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "res_other", "res_occ", "res_abd", "AS-RESIDENCE", "RES_OTHER", "RES_OCC", "RES_ABD" };

        // Canonical residence OD table names (use the colon form)
        private const string RES_OD_CREATE_DEFAULT = "Block:res_other";
        private const string RES_OD_PRIMARY_TABLE = "Block:res_other";



        private static ObjectId EnsureLayerGetId(Database db, string layerName, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = layerName };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                return ltr.ObjectId;
            }
            return lt[layerName];
        }

        // Normalizes table name for comparison: lower-case, remove any ':' after "block"
        private static string NormOdTableName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();
            if (s.StartsWith("OD:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(3);
            return s; // keep â€œBlock:res_otherâ€ distinct from â€œBlockres_otherâ€
        }

        // =========================================================================
        // RESINDEXV â€” Build vertex index (JSONL) from Master_Sections.dwg
        // =========================================================================
        [CommandMethod("ResidenceSync", "RESINDEXV", CommandFlags.Modal)]
        public void BuildVertexIndex()
        {
            Editor ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;

            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterSectionsPath = GetMasterSectionsPath(zone);
            if (!File.Exists(masterSectionsPath))
            {
                ed?.WriteMessage($"\nRESINDEXV: Master sections DWG not found: {masterSectionsPath}");
                return;
            }

            string outJsonPath = GetMasterSectionsIndexJsonPath(zone);
            string outCsvPath = GetMasterSectionsIndexCsvPath(zone);

            DocumentCollection docs = AcadApp.DocumentManager;
            Document master = GetOpenDocumentByPath(docs, masterSectionsPath);
            bool openedHere = false;
            if (master == null)
            {
                master = docs.Open(masterSectionsPath, false);
                openedHere = true;
            }

            try
            {
                using (master.LockDocument())
                {
                    var project = HostMapApplicationServices.Application?.Projects?.GetProject(master);
                    if (project == null)
                    {
                        ed?.WriteMessage("\nRESINDEXV: Map 3D project unavailable.");
                        return;
                    }

                    Tables tables = project.ODTables;
                    var searchOrder = BuildOdTableSearchOrder(tables);

                    // Pick "best" outline per section key: largest AABB area; store all vertices
                    var bestByKey = new Dictionary<string, (Aabb2d aabb, ObjectId entId)>(StringComparer.OrdinalIgnoreCase);

                    using (Transaction tr = master.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(master.Database.BlockTableId, DbOpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                        int scanned = 0;
                        foreach (ObjectId id in ms)
                        {
                            var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                            if (!IsPolylineEntity(ent)) continue;

                            if (!TryGetEntityAabb(master.Database, id, tr, out var aabb)) continue;

                            foreach (string tn in searchOrder)
                            {
                                if (string.IsNullOrWhiteSpace(tn)) continue;
                                using (OdTable t = tables[tn])
                                {
                                    if (t == null) continue;
                                    var defs = t.FieldDefinitions;

                                    using (Records recs = t.GetObjectTableRecords(0, ent.ObjectId, OdOpenMode.OpenForRead, true))
                                    {
                                        if (recs == null || recs.Count == 0) continue;

                                        foreach (Record rec in recs)
                                        {
                                            string sec = ReadOd(defs, rec, new[] { "SEC", "SECTION" }, MapValueToString);
                                            string twp = ReadOd(defs, rec, new[] { "TWP", "TOWNSHIP" }, MapValueToString);
                                            string rge = ReadOd(defs, rec, new[] { "RGE", "RANGE" }, MapValueToString);
                                            string mer = ReadOd(defs, rec, new[] { "MER", "MERIDIAN", "M" }, MapValueToString);

                                            string normSec = NormStr(sec);
                                            string normTwp = NormStr(twp);
                                            string normRge = NormStr(rge);
                                            string normMer = NormStr(mer);

                                            if (string.IsNullOrWhiteSpace(normSec) ||
                                                string.IsNullOrWhiteSpace(normTwp) ||
                                                string.IsNullOrWhiteSpace(normRge) ||
                                                string.IsNullOrWhiteSpace(normMer))
                                                continue;

                                            if (!MeridianMatchesZone(zone, normMer))
                                                continue;

                                            string key = $"{normSec}|{normTwp}|{normRge}|{normMer}";

                                            double area = (aabb.MaxX - aabb.MinX) * (aabb.MaxY - aabb.MinY);
                                            if (!bestByKey.TryGetValue(key, out var cur) ||
                                                area > (cur.aabb.MaxX - cur.aabb.MinX) * (cur.aabb.MaxY - cur.aabb.MinY))
                                            {
                                                bestByKey[key] = (aabb, id);
                                            }
                                        }
                                    }
                                }
                            }

                            scanned++;
                            if ((scanned % 10000) == 0) ed?.WriteMessage($"\nRESINDEXV: scanned {scanned} entities...");
                        }

                        tr.Commit();
                    }

                    // Write JSONL + CSV with all vertices of the chosen polyline per key
                    Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? "");
                    Directory.CreateDirectory(Path.GetDirectoryName(outCsvPath) ?? "");
                    using (var swJson = new StreamWriter(new FileStream(outJsonPath, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    using (var swCsv = new StreamWriter(new FileStream(outCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read)))
                    using (Transaction tr = master.TransactionManager.StartTransaction())
                    {
                        swCsv.WriteLine("ZONE,SEC,TWP,RGE,MER,minx,miny,maxx,maxy");

                        foreach (var kv in bestByKey)
                        {
                            var ent = tr.GetObject(kv.Value.entId, DbOpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // Extract ordered vertices (WCS; flatten Z)
                            var verts = new List<Point3d>();
                            bool closed = false;

                            if (ent is Polyline pl)
                            {
                                for (int i = 0; i < pl.NumberOfVertices; i++) verts.Add(pl.GetPoint3dAt(i));
                                closed = pl.Closed;
                            }
                            else if (ent is Polyline2d pl2)
                            {
                                foreach (ObjectId vId in pl2)
                                {
                                    var v = (Vertex2d)tr.GetObject(vId, DbOpenMode.ForRead);
                                    verts.Add(v.Position);
                                }
                                closed = pl2.Closed;
                            }
                            else if (ent is Polyline3d pl3)
                            {
                                foreach (ObjectId vId in pl3)
                                {
                                    var v = (PolylineVertex3d)tr.GetObject(vId, DbOpenMode.ForRead);
                                    verts.Add(new Point3d(v.Position.X, v.Position.Y, 0));
                                }
                                closed = pl3.Closed;
                            }
                            else
                            {
                                continue;
                            }

                            var aabb = kv.Value.aabb;
                            string[] parts = kv.Key.Split('|'); // SEC|TWP|RGE|MER
                            string sec = parts.Length > 0 ? parts[0] : string.Empty;
                            string twp = parts.Length > 1 ? parts[1] : string.Empty;
                            string rge = parts.Length > 2 ? parts[2] : string.Empty;
                            string mer = parts.Length > 3 ? parts[3] : string.Empty;

                            var ic = CultureInfo.InvariantCulture;
                            swCsv.WriteLine(string.Format(ic, "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                                (int)zone, sec, twp, rge, mer,
                                aabb.MinX, aabb.MinY, aabb.MaxX, aabb.MaxY));

                            // Build JSON (no external deps)
                            var sb = new System.Text.StringBuilder(256 + verts.Count * 24);
                            sb.Append('{');
                            sb.AppendFormat(ic, "\"ZONE\":{0},\"SEC\":\"{1}\",\"TWP\":\"{2}\",\"RGE\":\"{3}\",\"MER\":\"{4}\",",
                                (int)zone, sec, twp, rge, mer);
                            sb.AppendFormat(ic, "\"AABB\":{{\"minx\":{0},\"miny\":{1},\"maxx\":{2},\"maxy\":{3}}},",
                                aabb.MinX, aabb.MinY, aabb.MaxX, aabb.MaxY);
                            sb.AppendFormat("\"Closed\":{0},", closed ? "true" : "false");
                            sb.Append("\"Verts\":[");
                            for (int i = 0; i < verts.Count; i++)
                            {
                                if (i > 0) sb.Append(',');
                                var p = verts[i];
                                sb.AppendFormat(ic, "[{0},{1}]", p.X, p.Y);
                            }
                            sb.Append("]}");

                            swJson.WriteLine(sb.ToString());
                        }
                    }

                    ed?.WriteMessage($"\nRESINDEXV: Wrote {bestByKey.Count} section outline(s) â†’ {outJsonPath} (JSON) and {outCsvPath} (CSV).");
                }
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\nRESINDEXV: Failed: {ex.Message}");
            }
            finally
            {
                if (openedHere)
                {
                    try { master.CloseAndDiscard(); } catch { /* ignore */ }
                }
            }
        }

        // =========================================================================
        // BUILDSEC â€” Rebuild one section exactly as scanned + insert residences
        // =========================================================================
        [CommandMethod("ResidenceSync", "BUILDSEC", CommandFlags.Modal)]
        public void BuildSectionFromVertices()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!ConfirmUtm(ed))
            {
                return;
            }

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!File.Exists(idxPath))
            {
                ed.WriteMessage($"\nBUILDSEC: Vertex index not found for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            VertexIndexRecord rec;
            if (!TryReadSectionFromJsonl(idxPath, key, out rec))
            {
                ed.WriteMessage("\nBUILDSEC: Section not found in vertex index.");
                return;
            }

            using (doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database, "L-USEC", tr);
                EnsureLayer(doc.Database, "L-QSEC", tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                // Outline from vertices (at master coords)
                var pl = new Polyline(rec.verts.Count);
                for (int i = 0; i < rec.verts.Count; i++)
                {
                    var p = rec.verts[i];
                    pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                }
                pl.Closed = rec.closed;
                pl.Layer = "L-USEC";
                // Ensure the polyline uses ByLayer color so it doesn't inherit a previous commandâ€™s color
                pl.ColorIndex = 256;
                ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);

                if (TryGetQuarterAnchorsByEdgeMedianVertexChain(rec.verts,
                        out Point3d topV, out Point3d botV,
                        out Point3d leftV, out Point3d rightV))
                {
                    // Create quarter lines and ensure they use ByLayer color (ColorIndex 256)
                    var qv = new Line(topV, botV) { Layer = "L-QSEC", ColorIndex = 256 };
                    var qh = new Line(leftV, rightV) { Layer = "L-QSEC", ColorIndex = 256 };
                    ms.AppendEntity(qv); tr.AddNewlyCreatedDBObject(qv, true);
                    ms.AppendEntity(qh); tr.AddNewlyCreatedDBObject(qh, true);

                    // Insert section label block at the intersection of quarter lines
                    var center = new Point3d(
                        0.5 * (topV.X + botV.X),
                        0.5 * (leftV.Y + rightV.Y),
                        0);
                    InsertSectionLabelBlock(ms, bt, tr, ed, center, key);
                }

                tr.Commit();
            }

            ed.WriteMessage("\nBUILDSEC: Section drawn from vertex index at master coordinates.");
        }

        private static bool ConfirmUtm(Editor ed)
        {
            var opts = new PromptKeywordOptions("\nARE YOU IN UTM? [Yes/No]: ", "Yes No")
            {
                AllowArbitraryInput = false,
                AllowNone = false
            };

            opts.Keywords.Default = "Yes";

            var res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK)
            {
                return false;
            }

            if (string.Equals(res.StringResult, "No", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\nBUILDSEC: Command cancelled (not in UTM).");
                return false;
            }

            return true;
        }

        private static void InsertSectionLabelBlock(
            BlockTableRecord ms,
            BlockTable bt,
            Transaction tr,
            Editor ed,
            Point3d position,
            SectionKey key)
        {
            const string blockName = "L-SECLBL";

            if (!bt.Has(blockName))
            {
                ed?.WriteMessage($"\nBUILDSEC: Block '{blockName}' not found; skipped section label.");
                return;
            }

            var btrId = bt[blockName];
            var br = new BlockReference(position, btrId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
            if (btr.HasAttributeDefinitions)
            {
                foreach (ObjectId id in btr)
                {
                    if (!(tr.GetObject(id, DbOpenMode.ForRead) is AttributeDefinition ad)) continue;
                    if (ad.Constant) continue;

                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);
                }
            }

            SetBlockAttribute(br, "SEC", key.Section);
            SetBlockAttribute(br, "TWP", key.Township);
            SetBlockAttribute(br, "RGE", key.Range);
            SetBlockAttribute(br, "MER", key.Meridian);
        }

        // =========================================================================
        // PULLRESV â€” Pull residence blocks (with OD) from master into this DWG for a section
        // =========================================================================
        [CommandMethod("ResidenceSync", "PULLRESV", CommandFlags.Modal)]
        public void PullResidencesForSection()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!TryReadSectionFromJsonl(idxPath, key, out VertexIndexRecord rec))
            {
                ed.WriteMessage($"\nPULLRESV: Section not found in vertex index for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            string masterResidencesPath = GetMasterResidencesPath(key.Zone);

            if (!File.Exists(masterResidencesPath))
            {
                ed.WriteMessage($"\nPULLRESV: Master residences DWG not found: {masterResidencesPath}.");
                return;
            }

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                EnsureLayer(doc.Database, RESIDENCE_LAYER, tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                int inserted = 0;

                // Open master db and collect recognized residence blocks inside the section polygon
                using (var masterDb = new Database(false, true))
                {
                    masterDb.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                    masterDb.CloseInput(true);

                    // Collect only recognized residence BLOCKS (ignore legacy DBPOINTs here)
                    ObjectIdCollection srcIds = new ObjectIdCollection();
                    using (var trM = masterDb.TransactionManager.StartTransaction())
                    {
                        var btM = (BlockTable)trM.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                        var msM = (BlockTableRecord)trM.GetObject(btM[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                        foreach (ObjectId id in msM)
                        {
                            var ent = trM.GetObject(id, DbOpenMode.ForRead) as Entity;
                            if (!(ent is BlockReference brM)) continue;

                            // filter by block name
                            string bn = GetEffectiveBlockName(brM, trM);
                            if (!RES_BLOCK_NAMES.Contains(bn)) continue;

                            // inside keyed section?
                            if (PointInPolygon2D(rec.verts, brM.Position.X, brM.Position.Y))
                                srcIds.Add(id);
                        }
                        trM.Commit();
                    }

                    if (srcIds.Count > 0)
                    {
                        // Clone with OD into this current drawing
                        var idMap = new IdMapping();
                        doc.Database.WblockCloneObjects(srcIds, ms.ObjectId, idMap, DuplicateRecordCloning.Replace, false);

                        // Force cloned items onto Z-RESIDENCE
                        ObjectId resLayerId = ForceLayerVisible(doc.Database, RESIDENCE_LAYER, tr);
                        foreach (IdPair p in idMap)
                        {
                            if (!p.IsCloned) continue;
                            var ent = tr.GetObject(p.Value, DbOpenMode.ForWrite) as Entity;
                            if (ent == null) continue;
                            ent.LayerId = resLayerId; // safer than name
                            inserted++;
                        }
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\nPULLRESV: Inserted {inserted} residence block(s) with OD.");
            }

            ed.Regen();
        }

        [CommandMethod("ResidenceSync", "PUSHRESS", CommandFlags.Modal)]
        public void PushResidencesFromScaledSection()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            if (!PromptSectionKey(ed, out SectionKey key)) return;

            string masterResidencesPath = GetMasterResidencesPath(key.Zone);
            if (IsMasterPointsOpen(masterResidencesPath))
            {
                ed.WriteMessage($"\nPUSHRESS: '{Path.GetFileName(masterResidencesPath)}' is open. Close it and try again.");
                return;
            }

            if (!File.Exists(masterResidencesPath))
            {
                ed.WriteMessage($"\nPUSHRESS: Master residences DWG not found: {masterResidencesPath}.");
                return;
            }

            string idxPath = GetMasterSectionsIndexJsonPath(key.Zone);
            if (!TryReadSectionFromJsonl(idxPath, key, out VertexIndexRecord rec))
            {
                ed.WriteMessage($"\nPUSHRESS: Section not found in vertex index for Zone {FormatZoneNumber(key.Zone)}. Run RESINDEXV first.");
                return;
            }

            if (!TryGetSectionTopCorners(rec.verts, out Point3d masterTopLeft, out Point3d masterTopRight))
            {
                ed.WriteMessage("\nPUSHRESS: Unable to determine top edge in master section outline.");
                return;
            }

            var tlRes = ed.GetPoint(new PromptPointOptions("\nPick TOP LEFT of the section in scaled linework: ") { AllowNone = false });
            if (tlRes.Status != PromptStatus.OK) return;
            var trRes = ed.GetPoint(new PromptPointOptions("\nPick TOP RIGHT of the section in scaled linework: ")
            {
                UseBasePoint = true,
                BasePoint = tlRes.Value,
                AllowNone = false
            });
            if (trRes.Status != PromptStatus.OK) return;

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem.Inverse();
            Point3d localTL_W = tlRes.Value.TransformBy(ucsToWcs);
            Point3d localTR_W = trRes.Value.TransformBy(ucsToWcs);

            Vector3d vLocalW = localTR_W - localTL_W;
            Vector3d vMaster = masterTopRight - masterTopLeft;
            double lenLocal = vLocalW.Length, lenMaster = vMaster.Length;
            if (lenLocal < 1e-9 || lenMaster < 1e-9) { ed.WriteMessage("\nPUSHRESS: Degenerate corner picks."); return; }

            double scale = lenMaster / lenLocal;
            double angLoc = Math.Atan2(vLocalW.Y, vLocalW.X);
            double angMas = Math.Atan2(vMaster.Y, vMaster.X);
            double dTheta = angMas - angLoc;

            Point3d trProjected = TransformScaledPoint(localTR_W, localTL_W, masterTopLeft, scale, dTheta);
            double err = trProjected.DistanceTo(new Point3d(masterTopRight.X, masterTopRight.Y, 0));
            if (err > TRANSFORM_VALIDATION_TOL)
            {
                ed.WriteMessage($"\nPUSHRESS: Corner picks donâ€™t align (err {err:F3} m > {TRANSFORM_VALIDATION_TOL:F3} m).");
                return;
            }
            ed.WriteMessage($"\nPUSHRESS: using scale={scale:F6}, rot={(dTheta * 180.0 / Math.PI):F3}Â°, TR err={err:F3} m.");

            var sel = ed.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect residence blocks to push (res_other/res_occ/res_abd): " },
                new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT,POINT") })
            );
            if (sel.Status != PromptStatus.OK || sel.Value == null || sel.Value.Count == 0)
            {
                ed.WriteMessage("\nPUSHRESS: Nothing selected.");
                return;
            }

            string jobFromThisDwg = Path.GetFileNameWithoutExtension(doc.Name) ?? "";

            // Build items (attributes only)
            var items = CollectPushItemsFromSelection(
                doc, sel.Value, localTL_W, masterTopLeft, scale, dTheta, jobFromThisDwg, out int missingAttr, Matrix3d.Identity);

            if (items.Count == 0) { ed.WriteMessage("\nPUSHRESS: No usable items."); return; }

            var result = UpsertResidenceBlocksInMaster(items, jobFromThisDwg, masterResidencesPath, ed);
            ed.WriteMessage($"\nPUSHRESS: Finished â€” moved {result.moved}, inserted {result.inserted}. (Attrs only; master saved off-screen.)");
        }
        // Treat â€œinside OR on boundary within tolâ€ as inside.


        [CommandMethod("ResidenceSync", "SURFDEV", CommandFlags.Modal)]
        public void BuildSurfaceDevelopment()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            // 1) Center section
            if (!PromptSectionKey(ed, out SectionKey centerKey)) return;

            // 2) Grid size
            string gridSizeKey = "5x5";
            {
                var gridOpts = new PromptKeywordOptions("\nPick grid size [3x3/5x5/7x7/9x9]: ")
                {
                    AllowNone = true,
                    AllowArbitraryInput = false
                };
                gridOpts.Keywords.Add("3x3");
                gridOpts.Keywords.Add("5x5");
                gridOpts.Keywords.Add("7x7");
                gridOpts.Keywords.Add("9x9");
                gridOpts.Keywords.Default = "5x5";

                var gridRes = ed.GetKeywords(gridOpts);
                if (gridRes.Status == PromptStatus.Cancel) return;
                if (gridRes.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(gridRes.StringResult))
                    gridSizeKey = gridRes.StringResult;
                else if (gridRes.Status == PromptStatus.None)
                    gridSizeKey = gridOpts.Keywords.Default ?? "5x5";
            }

            int gridN = 5;
            if (!string.IsNullOrWhiteSpace(gridSizeKey))
            {
                var p = gridSizeKey.Trim();
                int x = p.IndexOf('x');
                string nStr = (x > 0) ? p.Substring(0, x) : p;
                if (int.TryParse(nStr, out int n) && (n == 3 || n == 5 || n == 7 || n == 9))
                    gridN = n;
            }

            // 3) Map scale
            var pko = new PromptKeywordOptions("\nPick scale [50k/30k/25k/20k]: ")
            {
                AllowNone = true,
                AllowArbitraryInput = false
            };
            pko.Keywords.Add("50k");
            pko.Keywords.Add("30k");
            pko.Keywords.Add("25k");
            pko.Keywords.Add("20k");
            pko.Keywords.Default = "50k";
            var kres = ed.GetKeywords(pko);
            if (kres.Status == PromptStatus.Cancel) return;
            string scaleKey = ((kres.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(kres.StringResult))
                ? kres.StringResult
                : (pko.Keywords.Default ?? "50k")).ToLowerInvariant();

            // 4) Surveyed vs Unsurveyed â†’ outline layer
            var pko2 = new PromptKeywordOptions("\nIs the development Surveyed or Unsurveyed? [Surveyed/Unsurveyed]: ")
            { AllowNone = true };
            pko2.Keywords.Add("Surveyed");
            pko2.Keywords.Add("Unsurveyed");
            pko2.Keywords.Default = "Unsurveyed";
            var kres2 = ed.GetKeywords(pko2);
            bool isSurveyed = (kres2.Status == PromptStatus.OK &&
                               string.Equals(kres2.StringResult, "Surveyed", StringComparison.OrdinalIgnoreCase));
            string outlineLayer = isSurveyed ? "L-SEC" : "L-USEC";

            // 5) Insert residences?
            var pko3 = new PromptKeywordOptions("\nInsert residence objects (blocks/points) from master? [No/Yes]: ")
            { AllowNone = true };
            pko3.Keywords.Add("No");
            pko3.Keywords.Add("Yes");
            pko3.Keywords.Default = "No";
            var kres3 = ed.GetKeywords(pko3);
            bool insertResidences = (kres3.Status == PromptStatus.OK &&
                                     string.Equals(kres3.StringResult, "Yes", StringComparison.OrdinalIgnoreCase));

            // 6) Insertion point â€” convert PICK (UCS) to WCS **using the INVERSE** of CUCS
            var pIns = ed.GetPoint("\nPick insertion point (centre of middle section): ");
            if (pIns.Status != PromptStatus.OK) return;
            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem.Inverse(); // â† correct
            Point3d insertCenter = pIns.Value.TransformBy(ucsToWcs);      // UCS â†’ WCS

            // Units/scales
            double unitsPerKm;
            double secTextHt;
            double surfDevLinetypeScale;
            switch (scaleKey)
            {
                case "20k":
                    unitsPerKm = 250.0;
                    secTextHt = 37.5;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "25k":
                    unitsPerKm = 200.0;
                    secTextHt = 30.0;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "30k":
                    unitsPerKm = 166.66666666666666; // 100 * (50/30)
                    secTextHt = 25.0;
                    surfDevLinetypeScale = 0.50;
                    break;
                case "50k":
                default:
                    unitsPerKm = 100.0;
                    secTextHt = 15.0;
                    surfDevLinetypeScale = 0.25;
                    break;
            }
            double unitsPerMetre = unitsPerKm / 1000.0;

            // Index path
            string idxPath = GetMasterSectionsIndexJsonPath(centerKey.Zone);
            if (!File.Exists(idxPath))
            {
                ed.WriteMessage($"\nSURFDEV: Vertex index not found for Zone {FormatZoneNumber(centerKey.Zone)}. Run RESINDEXV first.");
                return;
            }

            // Load center record
            if (!TryReadSectionFromJsonl(idxPath, centerKey, out VertexIndexRecord centerRec))
            {
                ed.WriteMessage($"\nSURFDEV: Center section not found in vertex index for Zone {FormatZoneNumber(centerKey.Zone)}.");
                return;
            }

            // MASTER centre (AABB centre, WCS)
            Point3d centerMm = new Point3d(
                (centerRec.aabb.MinX + centerRec.aabb.MaxX) * 0.5,
                (centerRec.aabb.MinY + centerRec.aabb.MaxY) * 0.5, 0);

            // One transform for EVERYTHING (outlines, Q-lines, labels, residences)
            Matrix3d xform = BuildMasterToSketchTransform(centerMm, insertCenter, unitsPerMetre);

            // (Optional) quick sanity print
            ed.WriteMessage($"\n[SURFDEV] insertWCS=({insertCenter.X:0.###},{insertCenter.Y:0.###})  centerMm=({centerMm.X:0.###},{centerMm.Y:0.###})  units/m={unitsPerMetre:0.###}");

            // DLS serpentine (row 0=north, col 0=west)
            int[,] serp =
            {
        {31,32,33,34,35,36},
        {30,29,28,27,26,25},
        {19,20,21,22,23,24},
        {18,17,16,15,14,13},
        { 7, 8, 9,10,11,12},
        { 6, 5, 4, 3, 2, 1}
    };

            // Locate center
            if (!int.TryParse(centerKey.Section.TrimStart('0'), out int secNum))
            { ed.WriteMessage("\nSURFDEV: SEC not numeric."); return; }

            int selRow = -1, selCol = -1;
            for (int r = 0; r < 6 && selRow < 0; r++)
                for (int c = 0; c < 6; c++)
                    if (serp[r, c] == secNum) { selRow = r; selCol = c; break; }
            if (selRow < 0) { ed.WriteMessage("\nSURFDEV: SEC not in 1..36."); return; }

            // Neighbor key (Townshipâ†‘ north, Rangeâ†‘ west)
            SectionKey NeighborKey(int dRow, int dCol)
            {
                int targetRow = selRow + dRow;
                int targetCol = selCol + dCol;

                int twp; int rge;
                if (!int.TryParse(centerKey.Township.Trim(), out twp)) twp = 0;
                if (!int.TryParse(centerKey.Range.Trim(), out rge)) rge = 0;

                while (targetRow < 0) { twp += 1; targetRow += 6; }
                while (targetRow > 5) { twp -= 1; targetRow -= 6; }
                while (targetCol < 0) { rge += 1; targetCol += 6; }
                while (targetCol > 5) { rge -= 1; targetCol -= 6; }

                if (twp <= 0 || rge <= 0) return default(SectionKey);

                int s = serp[targetRow, targetCol];
                return new SectionKey(centerKey.Zone,
                                      s.ToString(CultureInfo.InvariantCulture),
                                      twp.ToString(CultureInfo.InvariantCulture),
                                      rge.ToString(CultureInfo.InvariantCulture),
                                      centerKey.Meridian);
            }

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                // Layers
                EnsureLayer(doc.Database, outlineLayer, tr);
                EnsureLayer(doc.Database, "L-QSEC", tr);
                EnsureLayer(doc.Database, "S-7", tr);
                if (insertResidences) EnsureLayer(doc.Database, RESIDENCE_LAYER, tr);

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                // Keep master-space polygons for residence hit-testing
                var sectionPolysMaster = new List<List<Point3d>>();
                int secCount = 0;

                int half = Math.Max(1, (gridN - 1) / 2);

                // ---- NxN window around centre ----
                for (int dRow = -half; dRow <= half; dRow++)
                {
                    for (int dCol = -half; dCol <= half; dCol++)
                    {
                        SectionKey k2 = NeighborKey(dRow, dCol);
                        if (string.IsNullOrEmpty(k2.Section)) continue;

                        if (!TryReadSectionFromJsonl(idxPath, k2, out VertexIndexRecord rec))
                            continue;

                        sectionPolysMaster.Add(rec.verts); // master coords

                        // Outline (build in master, then transform)
                        var pl = new Polyline(rec.verts.Count);
                        for (int i = 0; i < rec.verts.Count; i++)
                        {
                            var p = rec.verts[i]; // master
                            pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                        }
                        pl.Closed = rec.closed;
                        pl.Layer = outlineLayer;
                        pl.ColorIndex = 253;
                        ms.AppendEntity(pl); tr.AddNewlyCreatedDBObject(pl, true);
                        pl.TransformBy(xform);
                        pl.LinetypeScale = surfDevLinetypeScale;

                        // Quarter-line anchors (master) â†’ entities â†’ transform
                        if (TryGetQuarterAnchorsByEdgeMedianVertexChain(rec.verts,
                                out Point3d topV, out Point3d botV, out Point3d leftV, out Point3d rightV))
                        {
                            var qv = new Line(topV, botV) { Layer = "L-QSEC", ColorIndex = 253 };
                            var qh = new Line(leftV, rightV) { Layer = "L-QSEC", ColorIndex = 253 };
                            ms.AppendEntity(qv); tr.AddNewlyCreatedDBObject(qv, true);
                            ms.AppendEntity(qh); tr.AddNewlyCreatedDBObject(qh, true);
                            qv.TransformBy(xform);
                            qh.TransformBy(xform);
                            qv.LinetypeScale = surfDevLinetypeScale;
                            qh.LinetypeScale = surfDevLinetypeScale;

                            // Label at cross center (compute in master, then transform point)
                            Point3d centerM = new Point3d(
                                (topV.X + botV.X + leftV.X + rightV.X) * 0.25,
                                (topV.Y + botV.Y + leftV.Y + rightV.Y) * 0.25, 0);
                            AddMaskedLabel(ms, tr, centerM.TransformBy(xform), k2.Section.TrimStart('0'), secTextHt, "S-7");
                        }
                        else
                        {
                            // Fallback: label at master AABB center â†’ transform point
                            double lx = rec.verts.Min(v => v.X), ly = rec.verts.Min(v => v.Y);
                            double ux = rec.verts.Max(v => v.X), uy = rec.verts.Max(v => v.Y);
                            Point3d ctrM = new Point3d((lx + ux) * 0.5, (ly + uy) * 0.5, 0);
                            AddMaskedLabel(ms, tr, ctrM.TransformBy(xform), k2.Section.TrimStart('0'), secTextHt, "S-7");
                        }

                        secCount++;
                    }
                }

                int inserted = 0;

                if (insertResidences)
                {
                    string masterResidencesPath = GetMasterResidencesPath(centerKey.Zone);

                    if (!File.Exists(masterResidencesPath))
                    {
                        ed.WriteMessage($"\nSURFDEV: Master residences DWG not found ({masterResidencesPath}); skipping residence insertion.");
                    }
                    else
                    {
                        using (var masterDb = new Database(false, true))
                        {
                            masterDb.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                            masterDb.CloseInput(true);

                            ObjectIdCollection srcIds = CollectResidenceSourceIds(masterDb, sectionPolysMaster);

                            if (srcIds.Count > 0)
                            {
                                var idMap = new IdMapping();
                                doc.Database.WblockCloneObjects(
                                    srcIds, ms.ObjectId, idMap, DuplicateRecordCloning.Replace, false);

                                // Dest ids strictly from inputs; skip null/erased
                                var destIds = new List<ObjectId>(srcIds.Count);
                                foreach (ObjectId sid in srcIds)
                                {
                                    if (!idMap.Contains(sid)) continue;
                                    var pair = idMap[sid];
                                    if (!pair.IsCloned) continue;
                                    var did = pair.Value;
                                    if (did.IsNull || did.IsErased) continue;
                                    destIds.Add(did);
                                }

                                // Transform only top-level ModelSpace BR/DBPoint
                                foreach (var did in destIds)
                                {
                                    Entity ent = null;
                                    try { ent = tr.GetObject(did, DbOpenMode.ForWrite, /*openErased*/ true) as Entity; }
                                    catch { continue; }
                                    if (ent == null || ent.IsErased) continue;
                                    if (ent.OwnerId != ms.ObjectId) continue;

                                    if (ent is BlockReference br)
                                    {
                                        br.TransformBy(xform);              // same transform as outlines
                                        br.Layer = RESIDENCE_LAYER;
                                        br.ScaleFactors = new Scale3d(5.0); // force uniform scale = 5
                                        inserted++;
                                    }
                                    else if (ent is DBPoint dp)
                                    {
                                        dp.TransformBy(xform);              // same transform as outlines
                                        dp.Layer = RESIDENCE_LAYER;
                                        inserted++;
                                    }
                                }

                                if (inserted > 0) EnsurePointStyleVisible();
                            }
                        }
                    }
                }

                tr.Commit();

                ed.WriteMessage($"\nSURFDEV: Built {gridN}Ã—{gridN} ({secCount} sections){(insertResidences ? $", inserted {inserted} residence object(s) (blocks/points, OD preserved)" : "")}. Outlines/Q-sec color 253; labels on S-7 with mask.");
            }

            ed.Regen();
        }

        private static double PointToSegDist2(double px, double py,
                                              double ax, double ay,
                                              double bx, double by)
        {
            double vx = bx - ax, vy = by - ay;
            double wx = px - ax, wy = py - ay;

            double c1 = vx * wx + vy * wy;
            if (c1 <= 0.0) return (px - ax) * (px - ax) + (py - ay) * (py - ay);

            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) return (px - bx) * (px - bx) + (py - by) * (py - by);

            double t = c1 / c2;
            double projx = ax + t * vx;
            double projy = ay + t * vy;
            double dx = px - projx, dy = py - projy;
            return dx * dx + dy * dy;
        }

        // What we push for each picked entity
        private sealed class PushItem
        {
            public Point3d Target;           // mapped (master WCS)
            public string BlockName;         // res_other/res_occ/res_abd (fallback = res_other)
            public string Desc;              // from OD (manual)
            public string Notes;             // from OD (manual)
            public string OdTable;           // table to use when writing (prefer record's table; else primary)
        }

        // Transform a source WCS point into master WCS
        private static Point3d MapScaledToMaster(Point3d sourceWcs, Point3d localTL, Point3d masterTL, double scale, double rot)
            => TransformScaledPoint(sourceWcs, localTL, masterTL, scale, rot);

        // Read OD from an entity (any of the supported tables). Returns true if we found one.
        // Read OD from an entity from any of our residence tables. Returns true if found.
        // Read OD from an entity from any of our residence tables. Returns true if found.
        // Read OD from an entity from any of our residence tables. Returns true if found.

        // C# 7.3-friendly helper (NO local functions)
        private static bool ProbeResidenceOd(
            FieldDefinitions defs,
            OdTable table,
            ObjectId entId,
            bool openXRecords,
            out string job,
            out string desc,
            out string notes)
        {
            job = desc = notes = null;

            using (Records recs = table.GetObjectTableRecords(0, entId, OdOpenMode.OpenForRead, openXRecords))
            {
                if (recs == null || recs.Count == 0) return false;

                foreach (Record r in recs)
                {
                    // Be permissive: JOB_NUM might be Character or accidentally an Integer in old data.
                    string j = ReadOd(defs, r, new[] { "JOB_NUM" }, mv =>
                    {
                        if (mv == null) return null;
                        if (mv.Type == OdDataType.Character) return mv.StrValue;
                        if (mv.Type == OdDataType.Integer) return mv.Int32Value.ToString(CultureInfo.InvariantCulture);
                        return mv.StrValue; // fallback
                    });

                    string d = ReadOd(defs, r, new[] { "DESCRIPTION" }, mv => mv?.StrValue);
                    string n = ReadOd(defs, r, new[] { "NOTES" }, mv => mv?.StrValue);

                    // Treat as a hit if any field actually exists on the record (empty string is OK)
                    if (j != null || d != null || n != null)
                    {
                        job = j; desc = d; notes = n;
                        return true;
                    }
                }
            }
            return false;
        }
        // ---------- ATTR READER (ADD THIS) ----------
        private static bool TryReadBlockAttributes(
            BlockReference br,
            out string job,
            out string desc,
            out string notes)
        {
            job = desc = notes = null;
            if (br == null) return false;

            var tm = br.Database?.TransactionManager;
            if (tm == null) return false;

            foreach (ObjectId attId in br.AttributeCollection)
            {
                var ar = tm.GetObject(attId, DbOpenMode.ForRead, false) as AttributeReference;
                if (ar == null) continue;
                var tag = ar.Tag ?? string.Empty;

                if (tag.Equals("JOB_NUM", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("JOBNUMBER", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("JOB", StringComparison.OrdinalIgnoreCase))
                {
                    if (job == null) job = ar.TextString;
                }
                else if (tag.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    if (desc == null) desc = ar.TextString;
                }
                else if (tag.Equals("NOTES", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("NOTE", StringComparison.OrdinalIgnoreCase) ||
                         tag.Equals("COMMENTS", StringComparison.OrdinalIgnoreCase))
                {
                    if (notes == null) notes = ar.TextString;
                }
            }
            return (job != null || desc != null || notes != null);
        }



        private static bool TrySetStringFieldByAliases(FieldDefinitions defs, Records recs, Record rec, string[] aliases, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;
                MapValue mv = rec[i];
                mv.Assign(value ?? string.Empty);
                recs.UpdateRecord(rec);
                return true;
            }
            return false;
        }

        private static bool TrySetStringFieldByAliases(FieldDefinitions defs, Record rec, string[] aliases, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;
                MapValue mv = rec[i];
                mv.Assign(value ?? string.Empty);
                return true;
            }
            return false;
        }

        // Set a block attribute (if present) to a value
        private static void SetBlockAttributes(BlockReference br, string job, string descOrNull, string notesOrNull)
        {
            if (br == null) return;
            SetBlockAttribute(br, "JOB_NUM", job ?? string.Empty);
            if (descOrNull != null) { SetBlockAttribute(br, "DESCRIPTION", descOrNull); SetBlockAttribute(br, "DESC", descOrNull); }
            if (notesOrNull != null) { SetBlockAttribute(br, "NOTES", notesOrNull); SetBlockAttribute(br, "NOTE", notesOrNull); }
        }

        // Build push items (reads OD from source; performs the scale/rotate/map)
        // Build push items (reads OD from source; performs the scale/rotate/map)
        // NOTE: wcsToUcs parameter is now unused; kept for signature compatibility.
        // Build push items. We no longer propagate DESCRIPTION/NOTES â€” only JOB_NUM.
        // Build push items (reads OD from source entities; performs the scale/rotate/map).
        // We propagate JOB_NUM, DESCRIPTION, and NOTES exactly as present in the source OD.
        // If a field is present but empty in source, we propagate empty (this will overwrite defaults in master).
        // ---------- BUILD PUSH LIST FROM SELECTION (ATTRS ONLY) ----------
        private List<PushItem> CollectPushItemsFromSelection(
            Document curDoc,
            SelectionSet sel,
            Point3d localTL_W,
            Point3d masterTL_W,
            double scale,
            double rot,
            string jobFromThisDwg,
            out int missingAttrCount,
            Matrix3d _unused)
        {
            missingAttrCount = 0;
            var items = new List<PushItem>();

            using (var tr = curDoc.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel)
                {
                    if (so?.ObjectId.IsNull != false) continue;
                    var ent = tr.GetObject(so.ObjectId, DbOpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Point3d srcWcs;
                    string blockName = "res_other";

                    if (ent is BlockReference br)
                    {
                        srcWcs = br.Position;
                        blockName = GetEffectiveBlockName(br, tr);
                        if (!RES_BLOCK_NAMES.Contains(blockName)) continue;

                        // Read attributes
                        string j, d, n;
                        if (!TryReadBlockAttributes(br, out j, out d, out n))
                            missingAttrCount++;

                        // Map to master
                        Point3d target = MapScaledToMaster(srcWcs, localTL_W, masterTL_W, scale, rot);

                        items.Add(new PushItem
                        {
                            Target = target,
                            BlockName = blockName,
                            // Always push JOB from DWG name; DESCRIPTION/NOTES from block (empty => overwrite)
                            Desc = d ?? string.Empty,
                            Notes = n ?? string.Empty,
                            OdTable = null // unused now
                        });
                    }
                    else if (ent is DBPoint dp)
                    {
                        // Points have no attrs â€” still allow position copy, empty desc/notes
                        srcWcs = dp.Position;
                        Point3d target = MapScaledToMaster(srcWcs, localTL_W, masterTL_W, scale, rot);

                        items.Add(new PushItem
                        {
                            Target = target,
                            BlockName = blockName,     // will insert res_other if needed
                            Desc = string.Empty,
                            Notes = string.Empty,
                            OdTable = null
                        });
                    }
                }
                tr.Commit();
            }
            // merge duplicates (within tolerance)
            return DeduplicateItems(items, DEDUPE_TOL);
        }
        // Deduplicate by target location within tolerance
        private static List<PushItem> DeduplicateItems(List<PushItem> items, double tol)
        {
            var outList = new List<PushItem>(items.Count);
            foreach (var it in items)
            {
                bool near = outList.Any(x =>
                {
                    double dx = x.Target.X - it.Target.X;
                    double dy = x.Target.Y - it.Target.Y;
                    return (dx * dx + dy * dy) <= tol * tol;
                });
                if (!near) outList.Add(it);
            }
            return outList;
        }

        // If the selection lacks OD, attach a default record on the *current* drawing
        // If the selection lacks OD, attach a minimal record (JOB only) on the *current* drawing

        // Upsert into Master_Residences.dwg: move if within REPLACE_TOL, else insert.
        // Writes OD immediately to the canonical master table ("Blockres_other") and mirrors attributes.
        // Never injects defaults: we write exactly what CollectPushItemsFromSelection provided.
        // Prefer matching BLOCKs over DBPOINTs when both within tolerance.
        // ---------- UPSERT TO MASTER (SIDE DB, ATTRS ONLY) ----------
        // ---------- UPSERT TO MASTER (SIDE DB, ATTRS ONLY) ----------
        private (int moved, int inserted) UpsertResidenceBlocksInMaster(
            List<PushItem> items, string jobFromThisDwg, string masterPointsPath, Editor ed)
        {
            int moved = 0, inserted = 0;

            // Ensure file exists
            if (!File.Exists(masterPointsPath))
            {
                using (var newDb = new Database(true, true))
                    newDb.SaveAs(masterPointsPath, DwgVersion.Current);
            }

            // Open master DWG as side database (no UI)
            using (var masterDb = new Database(false, true))
            {
                masterDb.ReadDwgFile(masterPointsPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                masterDb.CloseInput(true);

                using (var tr = masterDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForWrite);

                    // Ensure target layer exists and is visible
                    ObjectId resLayerId = ForceLayerVisible(masterDb, RESIDENCE_LAYER, tr);

                    // Index existing residence objects (blocks we recognize + legacy points)
                    var existing = new List<(ObjectId id, Point3d pos, bool isBlock)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, DbOpenMode.ForRead) is Entity e)) continue;

                        if (e is BlockReference br)
                        {
                            string bn = GetEffectiveBlockName(br, tr);
                            if (!RES_BLOCK_NAMES.Contains(bn)) continue;
                            existing.Add((id, br.Position, true));
                        }
                        else if (e is DBPoint dp)
                        {
                            existing.Add((id, dp.Position, false));
                        }
                    }

                    // Helper: ensure a block definition exists in *master* (clone from a source db if needed)
                    ObjectId EnsureBlockDef(string blockName, Database srcDb)
                    {
                        if (bt.Has(blockName)) return bt[blockName];

                        var src = srcDb ?? AcadApp.DocumentManager.MdiActiveDocument?.Database;
                        if (src == null) return ObjectId.Null;

                        using (var trS = src.TransactionManager.StartTransaction())
                        {
                            var btS = (BlockTable)trS.GetObject(src.BlockTableId, DbOpenMode.ForRead);
                            if (!btS.Has(blockName)) return ObjectId.Null;

                            var ids = new ObjectIdCollection { btS[blockName] };
                            var idMap = new IdMapping();
                            masterDb.WblockCloneObjects(ids, masterDb.BlockTableId, idMap,
                                                        DuplicateRecordCloning.Ignore, false);
                            trS.Commit();

                            // refresh local block table
                            bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                            return bt.Has(blockName) ? bt[blockName] : ObjectId.Null;
                        }
                    }

                    // Active drawing database to use as the source for block definitions
                    var activeDb = AcadApp.DocumentManager.MdiActiveDocument?.Database;

                    foreach (var it in items)
                    {
                        int idx = FindNearestIdxPreferBlocks(existing, it.Target, REPLACE_TOL);
                        if (idx >= 0)
                        {
                            // MOVE/UPDATE existing
                            var ex = existing[idx];
                            var eW = tr.GetObject(ex.id, DbOpenMode.ForWrite) as Entity;

                            if (eW is BlockReference brW)
                            {
                                var d = it.Target - brW.Position;
                                if (!d.IsZeroLength()) brW.TransformBy(Matrix3d.Displacement(d));
                                brW.LayerId = resLayerId;
                                brW.ScaleFactors = new Scale3d(5.0); // enforce uniform scale in master

                                // Write attributes (JOB from DWG name; DESC/NOTES from source item)
                                SetBlockAttributes(brW, jobFromThisDwg, it.Desc, it.Notes);
                            }
                            else if (eW is DBPoint dpW)
                            {
                                dpW.Position = it.Target;
                                dpW.LayerId = resLayerId;
                            }

                            existing[idx] = (ex.id, it.Target, ex.isBlock);
                            moved++;
                        }
                        else
                        {
                            // INSERT new block (preferred) or point if definition missing
                            ObjectId btrId = EnsureBlockDef(it.BlockName, activeDb);
                            if (btrId.IsNull)
                            {
                                var dbp = new DBPoint(it.Target) { LayerId = resLayerId };
                                ms.AppendEntity(dbp); tr.AddNewlyCreatedDBObject(dbp, true);
                                inserted++;
                                continue;
                            }

                            var br = new BlockReference(it.Target, btrId)
                            {
                                LayerId = resLayerId,
                                ScaleFactors = new Scale3d(5.0) // enforce uniform scale in master
                            };
                            ms.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true);

                            // Create attribute references and set values
                            var btrRec = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
                            if (btrRec.HasAttributeDefinitions)
                            {
                                foreach (ObjectId id in btrRec)
                                {
                                    var ad = tr.GetObject(id, DbOpenMode.ForRead) as AttributeDefinition;
                                    if (ad == null || ad.Constant) continue;

                                    var ar = new AttributeReference();
                                    ar.SetAttributeFromBlock(ad, br.BlockTransform);
                                    br.AttributeCollection.AppendAttribute(ar);
                                    tr.AddNewlyCreatedDBObject(ar, true);
                                }
                            }

                            SetBlockAttributes(br, jobFromThisDwg, it.Desc, it.Notes);
                            inserted++;
                        }
                    }

                    tr.Commit();
                }

                // Save side database back to disk (no UI)
                masterDb.SaveAs(masterPointsPath, DwgVersion.Current);
            }

            return (moved, inserted);
        }
        private bool IsMasterPointsOpen(string masterPointsPath)
        {
            return GetOpenDocumentByPath(AcadApp.DocumentManager, masterPointsPath) != null;
        }
        // For a NEW record (before AddRecord) â€“ no UpdateRecord() needed
        private static void SetStringField(FieldDefinitions defs, Record rec, string field, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!def.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                MapValue mv = rec[i];     // get existing MapValue
                mv.Assign(value ?? string.Empty);
                break;
            }
        }

        // For an EXISTING record opened via GetObjectTableRecords(OpenForWrite, â€¦)
        private static void SetStringField(FieldDefinitions defs, Records recs, Record rec, string field, string value)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (!def.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                MapValue mv = rec[i];     // get existing MapValue
                mv.Assign(value ?? string.Empty);
                recs.UpdateRecord(rec);   // <- IMPORTANT for updates
                break;
            }
        }

        [CommandMethod("ResidenceSync", "DUMPMASTER", CommandFlags.Modal)]
        public void DumpMasterSummary()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;
            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterResidencesPath = GetMasterResidencesPath(zone);
            if (!File.Exists(masterResidencesPath)) { ed?.WriteMessage($"\nDUMPMASTER: Master file not found ({masterResidencesPath})."); return; }

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

                    int nInserts = 0, nPoints = 0;
                    double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
                    Action<Point3d> acc = p => { if (p.X < minx) minx = p.X; if (p.Y < miny) miny = p.Y; if (p.X > maxx) maxx = p.X; if (p.Y > maxy) maxy = p.Y; };

                    foreach (ObjectId id in ms)
                    {
                        var e = tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Entity;
                        if (e is BlockReference br) { nInserts++; acc(br.Position); }
                        else if (e is DBPoint dp) { nPoints++; acc(dp.Position); }
                    }
                    tr.Commit();

                    ed?.WriteMessage($"\nDUMPMASTER (Zone {FormatZoneNumber(zone)}): INSERTs={nInserts}, POINTs={nPoints}, Extents=[{minx:0.###},{miny:0.###}]â€“[{maxx:0.###},{maxy:0.###}]");
                }
            }
        }
        [CommandMethod("ResidenceSync", "DUMPMASTERPLUS", CommandFlags.Modal)]
        public void DumpMasterPlus()
        {
            var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;
            if (!PromptZone(ed, out CoordinateZone zone)) return;

            string masterResidencesPath = GetMasterResidencesPath(zone);
            if (!File.Exists(masterResidencesPath)) { ed?.WriteMessage($"\nDUMPMASTER+: Master file not found ({masterResidencesPath})."); return; }

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(masterResidencesPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                    var byName = new Dictionary<string, (int count, List<Point3d> samples)>(StringComparer.OrdinalIgnoreCase);
                    int nPoints = 0;

                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, DbOpenMode.ForRead) is Entity e)) continue;

                        if (e is BlockReference br)
                        {
                            ObjectId btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
                            string name = btr?.Name ?? "<null>";

                            if (!byName.TryGetValue(name, out var tup))
                                tup = (0, new List<Point3d>(3));
                            tup.count++;
                            if (tup.samples.Count < 3) tup.samples.Add(br.Position);
                            byName[name] = tup;
                        }
                        else if (e is DBPoint)
                        {
                            nPoints++;
                        }
                    }

                    tr.Commit();

                    if (byName.Count == 0 && nPoints == 0)
                    {
                        ed?.WriteMessage("\nDUMPMASTER+: No INSERTs or DBPOINTs found.");
                        return;
                    }

                    ed?.WriteMessage($"\nDUMPMASTER+ (Zone {FormatZoneNumber(zone)}):");
                    foreach (var kv in byName.OrderBy(k => k.Key))
                    {
                        var smp = kv.Value.samples.Select(p => $"({p.X:0.###},{p.Y:0.###})");
                        ed?.WriteMessage($"\n  {kv.Key}: {kv.Value.count}  samples: {string.Join(", ", smp)}");
                    }
                    if (nPoints > 0)
                        ed?.WriteMessage($"\n  <DBPOINT>: {nPoints}");
                }
            }
        }
        // =========================================================================
        // Helpers (layers, labels, chains, JSONL, geometry)
        // =========================================================================

        private static bool IsPolylineEntity(Entity entity)
            => entity is Polyline || entity is Polyline2d || entity is Polyline3d;

        private List<string> BuildOdTableSearchOrder(Tables tables)
        {
            var names = new List<string>();
            if (tables == null) return names;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(PREFERRED_OD_TABLE) && tables.IsTableDefined(PREFERRED_OD_TABLE))
            {
                names.Add(PREFERRED_OD_TABLE);
                seen.Add(PREFERRED_OD_TABLE);
            }

            foreach (string name in tables.GetTableNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (seen.Add(name)) names.Add(name);
            }
            return names;
        }

        private static Document GetOpenDocumentByPath(DocumentCollection docs, string fullPath)
        {
            if (docs == null || string.IsNullOrWhiteSpace(fullPath)) return null;
            string target = NormalizePath(fullPath);

            foreach (Document d in docs)
            {
                if (d == null) continue;

                // Prefer the database filename (full path). Fallback to Name only if it is rooted.
                string candidate = null;
                try
                {
                    candidate = d.Database?.Filename;
                    if (!string.IsNullOrWhiteSpace(candidate))
                        candidate = NormalizePath(candidate);
                }
                catch { /* ignore */ }

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    try
                    {
                        string n = d.Name;
                        if (!string.IsNullOrWhiteSpace(n) && Path.IsPathRooted(n))
                            candidate = NormalizePath(n);
                    }
                    catch { /* ignore */ }
                }

                if (!string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        private bool TryGetEntityAabb(Database db, ObjectId id, Transaction tr, out Aabb2d aabb)
        {
            aabb = default;
            try
            {
                var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                if (ent == null) return false;

                try
                {
                    var ext = ent.GeometricExtents; // robust to rotation, bulges
                    aabb = new Aabb2d(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
                    return true;
                }
                catch
                {
                    // Fallback: vertex sweep
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    if (ent is Polyline pl)
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            var p = pl.GetPoint3dAt(i);
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else if (ent is Polyline2d pl2)
                    {
                        foreach (ObjectId vId in pl2)
                        {
                            var v = (Vertex2d)tr.GetObject(vId, DbOpenMode.ForRead);
                            var p = v.Position;
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else if (ent is Polyline3d pl3)
                    {
                        foreach (ObjectId vId in pl3)
                        {
                            var v = (PolylineVertex3d)tr.GetObject(vId, DbOpenMode.ForRead);
                            var p = new Point3d(v.Position.X, v.Position.Y, 0);
                            if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                        }
                    }
                    else
                    {
                        return false;
                    }

                    if (minX == double.MaxValue) return false;
                    aabb = new Aabb2d(minX, minY, maxX, maxY);
                    return true;
                }
            }
            catch { return false; }
        }

        private static void EnsurePointStyleVisible()
        {
            try
            {
                AcadApp.SetSystemVariable("PDMODE", 3);
                AcadApp.SetSystemVariable("PDSIZE", 0.8);
            }
            catch { /* ignore if locked */ }
        }

        private void EnsureLayer(Database db, string layerName, Transaction tr)
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!layerTable.Has(layerName))
            {
                layerTable.UpgradeOpen();
                var layerRecord = new LayerTableRecord { Name = layerName };
                layerTable.Add(layerRecord);
                tr.AddNewlyCreatedDBObject(layerRecord, true);
            }
        }

        // Force a layer's ACI color
        private void SetLayerColor(Database db, string layerName, short aci, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);
            if (!lt.Has(layerName)) return;
            var ltr = (LayerTableRecord)tr.GetObject(lt[layerName], DbOpenMode.ForWrite);
            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
        }

        // Masked label (MText with background fill)
        private void AddMaskedLabel(BlockTableRecord ms, Transaction tr,
                                    Point3d pos, string text, double height, string layer)
        {
            var mt = new MText
            {
                Location = pos,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = height,
                Contents = text,
                Layer = layer,
                ColorIndex = 256   // ByLayer for S-7
            };
            mt.BackgroundFill = true;
            mt.UseBackgroundColor = true;
            mt.BackgroundScaleFactor = 1.25;

            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }

        // Create (or normalize) a layer and return its ObjectId.
        // Ensures: ON, THAWED, UNLOCKED, PLOT = true. Name is case-insensitive.
        private static ObjectId ForceLayerVisible(Database db, string layerName, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, DbOpenMode.ForRead);

            ObjectId lid;
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = layerName };
                // Make it visible & plottable
                ltr.IsOff = false;
                ltr.IsFrozen = false;
                ltr.IsLocked = false;
                ltr.IsPlottable = true;

                lid = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            else
            {
                lid = lt[layerName];
                var ltr = (LayerTableRecord)tr.GetObject(lid, DbOpenMode.ForWrite);
                ltr.IsOff = false;
                ltr.IsFrozen = false;
                ltr.IsLocked = false;
                ltr.IsPlottable = true;
            }
            return lid;
        }

        // Returns all OD table names in this drawing that look like our residence tables
        // Returns OD tables that either match our canonical names OR
        // define at least one of the fields we care about (JOB_NUM/DESCRIPTION/NOTES).
        private static List<string> GetExistingResidenceOdTables(Tables tables)
        {
            var outNames = new List<string>();
            if (tables == null) return outNames;

            // 1) Exact preferred names first (colon form)
            foreach (var p in new[] { "Block:res_other", "Block:res_occ", "Block:res_abd" })
                if (tables.IsTableDefined(p)) outNames.Add(p);

            // 2) Fallback: colonless variants if present
            foreach (var q in new[] { "Blockres_other", "Blockres_occ", "Blockres_abd" })
                if (tables.IsTableDefined(q) && !outNames.Contains(q, StringComparer.OrdinalIgnoreCase))
                    outNames.Add(q);

            // 3) Any other table that happens to carry our fields (rare)
            foreach (string tn in tables.GetTableNames())
            {
                if (outNames.Contains(tn, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    using (var t = tables[tn])
                    {
                        var defs = t.FieldDefinitions;
                        for (int i = 0; i < defs.Count; i++)
                        {
                            var name = defs[i]?.Name;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (JOB_ALIASES.Concat(DESC_ALIASES).Concat(NOTES_ALIASES)
                                .Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            { outNames.Add(tn); break; }
                        }
                    }
                }
                catch { /* ignore */ }
            }
            return outNames;
        }


        // ---------- Side-chain selection (robust) ----------

        private struct EdgeInfo
        {
            public int Index;           // segment index (start vertex index)
            public Point3d A, B, Mid;   // endpoints and midpoint
            public Vector3d U;          // unit direction
            public double Len;          // length
        }

        private struct ChainInfo
        {
            public int Start;           // starting segment index
            public int SegCount;        // number of contiguous segments
            public double Score;        // average projection onto the orthogonal axis (north/east)
            public double TotalLen;     // total length of the chain
        }

        // Build contiguous chains by whichever axis an edge is closer to (no hard angle cutoff).
        private static List<ChainInfo> BuildChainsClosest(List<EdgeInfo> edges, Vector3d primary, Vector3d other)
        {
            var chains = new List<ChainInfo>();
            bool inChain = false;
            int start = -1;
            double sumProj = 0.0;
            int cnt = 0;
            double totLen = 0.0;

            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                double de = Math.Abs(e.U.DotProduct(primary));
                double dn = Math.Abs(e.U.DotProduct(other));
                bool isPrimary = de >= dn;

                if (isPrimary)
                {
                    if (!inChain) { inChain = true; start = e.Index; sumProj = 0.0; cnt = 0; totLen = 0.0; }
                    sumProj += (e.Mid - Point3d.Origin).DotProduct(other);
                    cnt++;
                    totLen += e.Len;
                }
                else
                {
                    if (inChain)
                    {
                        chains.Add(new ChainInfo { Start = start, SegCount = cnt, Score = (cnt > 0 ? sumProj / cnt : 0.0), TotalLen = totLen });
                        inChain = false;
                    }
                }
            }
            if (inChain)
                chains.Add(new ChainInfo { Start = start, SegCount = cnt, Score = (cnt > 0 ? sumProj / cnt : 0.0), TotalLen = totLen });

            // Merge wrap-around contiguous chains (last + first)
            if (chains.Count >= 2)
            {
                var first = chains[0];
                var last = chains[chains.Count - 1];
                if (first.Start == 0 && (last.Start + last.SegCount == edges.Count))
                {
                    int totalSeg = last.SegCount + first.SegCount;
                    double totalLen = last.TotalLen + first.TotalLen;
                    double avgScore = (totalSeg > 0) ? (last.Score * last.SegCount + first.Score * first.SegCount) / totalSeg : 0.0;
                    var merged = new ChainInfo { Start = last.Start, SegCount = totalSeg, Score = avgScore, TotalLen = totalLen };
                    chains[0] = merged;
                    chains.RemoveAt(chains.Count - 1);
                }
            }
            return chains;
        }

        // Helper: iterate vertex indices across a chain (m segments â†’ m+1 vertices)
        // ---- helpers used by the anchor picker ----
        private static IEnumerable<int> ChainVertexIndices(ChainInfo ch, int vertexCount)
        {
            for (int k = 0; k <= ch.SegCount; k++)
                yield return (ch.Start + k) % vertexCount;
        }
        private static double AxisProj(Point3d p, Vector3d axis)
        {
            return (p - Point3d.Origin).DotProduct(axis);
        }

        // Pick the median existing vertex in a chain w.r.t. a given axis (E for top/bottom).
        private static Point3d ChainMedianByAxis(List<Point3d> verts, ChainInfo ch, Vector3d axis)
        {
            var idxs = ChainVertexIndices(ch, verts.Count)
                       .OrderBy(i => AxisProj(verts[i], axis))
                       .ToList();
            return verts[idxs[idxs.Count / 2]];
        }

        // Pick the existing vertex in a chain whose projection on `axis` is nearest to `target`.
        private static Point3d ChainVertexNearestAxisValue(List<Point3d> verts, ChainInfo ch, Vector3d axis, double target)
        {
            int bestIdx = (ch.Start) % verts.Count;
            double best = double.MaxValue;

            foreach (int i in ChainVertexIndices(ch, verts.Count))
            {
                double d = Math.Abs(AxisProj(verts[i], axis) - target);
                if (d < best) { best = d; bestIdx = i; }
            }
            return verts[bestIdx];
        }

        // Convenience: set JOB_NUM always, and DESCRIPTION/NOTES when provided.
        // Writes to common tag variants so different block definitions are covered.


        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Returns the middle existing vertex (median-by-coordinate) from each true side chain.
        // Hardened against odd shapes and spiky outlines; falls back to oriented-box cross if needed.
        // Robust quarter-line anchors.
        // Top/Bottom choose the vertex closest to horizontal mid-span (E-target), not median index.
        // Left/Right already use nearest to N-target. Falls back to oriented-box cross if off-centre.
        private static bool TryGetQuarterAnchorsByEdgeMedianVertexChain(
            List<Point3d> verts,
            out Point3d topV, out Point3d bottomV, out Point3d leftV, out Point3d rightV)
        {
            topV = bottomV = leftV = rightV = Point3d.Origin;
            if (verts == null || verts.Count < 3) return false;

            // Build edges
            int n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (int i = 0; i < n; i++)
            {
                Point3d a = verts[i];
                Point3d b = verts[(i + 1) % n];
                Vector3d v = b - a;
                double len = v.Length;
                if (len <= 1e-9) continue;
                Vector3d u = new Vector3d(v.X / len, v.Y / len, 0);
                Point3d mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }
            if (edges.Count == 0) return false;

            // Choose a near-horizontal edge with highest average Y; fallback = longest edge.
            const double degTol = 12.0;
            double cosTol = Math.Cos(degTol * Math.PI / 180.0);
            EdgeInfo topEdge = default(EdgeInfo);
            double bestTopY = double.MinValue;
            foreach (var e in edges)
            {
                double horiz = Math.Abs(e.U.DotProduct(Vector3d.XAxis));
                double avgY = (e.A.Y + e.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY) { bestTopY = avgY; topEdge = e; }
            }
            if (bestTopY == double.MinValue)
                topEdge = edges.OrderByDescending(e => e.Len).First();

            // Local axes
            Vector3d east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12) return false;
            Vector3d north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            // Extents
            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3d dp = verts[i] - Point3d.Origin;
                double pe = dp.DotProduct(east);
                double pn = dp.DotProduct(north);
                if (pe < minE) minE = pe; if (pe > maxE) maxE = pe;
                if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
            }
            double spanE = Math.Max(1e-6, maxE - minE);
            double spanN = Math.Max(1e-6, maxN - minN);

            double bandTol = Math.Max(5.0, 0.01 * Math.Max(spanE, spanN)); // wider band

            // Chains
            var eChains = BuildChainsClosest(edges, east, north); // top/bottom
            var nChains = BuildChainsClosest(edges, north, east); // left/right
            if (eChains.Count == 0 || nChains.Count == 0) return false;

            bool TouchesTop(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxN - AxisProj(verts[idx], north) <= bandTol) return true;
                return false;
            }
            bool TouchesBottom(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (AxisProj(verts[idx], north) - minN <= bandTol) return true;
                return false;
            }
            bool TouchesLeft(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (AxisProj(verts[idx], east) - minE <= bandTol) return true;
                return false;
            }
            bool TouchesRight(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxE - AxisProj(verts[idx], east) <= bandTol) return true;
                return false;
            }

            ChainInfo PickTop(IEnumerable<ChainInfo> list) => list.OrderByDescending(c => c.Score).ThenByDescending(c => c.TotalLen).First();
            ChainInfo PickBottom(IEnumerable<ChainInfo> list) => list.OrderBy(c => c.Score).ThenByDescending(c => c.TotalLen).First();

            var top = eChains.Where(TouchesTop).DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).First()).First();
            var bottom = eChains.Where(TouchesBottom).DefaultIfEmpty(eChains.OrderBy(c => c.Score).First()).First();
            var left = nChains.Where(TouchesLeft).DefaultIfEmpty(nChains.OrderBy(c => c.Score).First()).First();
            var right = nChains.Where(TouchesRight).DefaultIfEmpty(nChains.OrderByDescending(c => c.Score).First()).First();

            // Use nearest-to-mid-span anchors (fixes wrong vertex picks)
            double Etarget = 0.5 * (minE + maxE);
            topV = ChainVertexNearestAxisValue(verts, top, east, Etarget);
            bottomV = ChainVertexNearestAxisValue(verts, bottom, east, Etarget);

            double Ntarget = 0.5 * (minN + maxN);
            leftV = ChainVertexNearestAxisValue(verts, left, north, Ntarget);
            rightV = ChainVertexNearestAxisValue(verts, right, north, Ntarget);

            // Sanity fallback
            double Emid = 0.5 * (AxisProj(leftV, east) + AxisProj(rightV, east));
            double Nmid = 0.5 * (AxisProj(topV, north) + AxisProj(bottomV, north));
            if (Math.Abs(Emid - 0.5 * (minE + maxE)) > 0.25 * spanE ||
                Math.Abs(Nmid - 0.5 * (minN + maxN)) > 0.25 * spanN)
            {
                Point3d FromEN(double E, double N) =>
                    new Point3d(east.X * E + north.X * N, east.Y * E + north.Y * N, 0);
                topV = FromEN(0.5 * (minE + maxE), maxN);
                bottomV = FromEN(0.5 * (minE + maxE), minN);
                leftV = FromEN(minE, 0.5 * (minN + maxN));
                rightV = FromEN(maxE, 0.5 * (minN + maxN));
            }
            return true;
        }

        // Effective (base) name for dynamic or regular blocks.
        private static string GetEffectiveBlockName(BlockReference br, Transaction tr)
        {
            ObjectId btrId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            var btr = (BlockTableRecord)tr.GetObject(btrId, DbOpenMode.ForRead);
            return btr?.Name ?? string.Empty;
        }

        // Pick up master residence blocks whose insertion point lies inside any of the polygons.
        private ObjectIdCollection CollectResidenceSourceIds(Database masterDb, List<List<Point3d>> sectionPolys)
        {
            var outIds = new ObjectIdCollection();
            if (sectionPolys == null || sectionPolys.Count == 0) return outIds;

            using (var tr = masterDb.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    Entity e = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    Point3d pos;
                    if (e is BlockReference br)
                    {
                        string name = GetEffectiveBlockName(br, tr);
                        if (!RES_BLOCK_NAMES.Contains(name)) continue;
                        pos = br.Position;
                    }
                    else if (e is DBPoint dp)
                    {
                        pos = dp.Position;
                    }
                    else continue;

                    // Inside any of the requested section polygons?
                    foreach (var poly in sectionPolys)
                    {
                        if (PointInPolygon2D(poly, pos.X, pos.Y))
                        {
                            outIds.Add(id);
                            break;
                        }
                    }
                }
                tr.Commit();
            }
            return outIds;
        }

        // Build the same mapping transform used by the outlines: scale about center, then shift.
        // IMPORTANT: Order matters. Right-most applies first. We want Scale then Displacement,
        // so the matrix product must be postShift * scaleAboutCenter.
        // Build the same mapping transform used by the outlines: scale about center, then shift.
        // IMPORTANT: matrix composition order â€” right-most is applied first.
        // Build the mapping transform for ALL geometry (outlines, Q-lines, labels, residences).
        // Right-most matrix applies first: ScaleAbout(center) then Displacement.
        // Build the mapping transform used by SURFDEV for ALL geometry.
        // Desired mapping: p' = insertCenter + s * (p - centerMm)
        // Matrix composition (right-most applies first):
        // p' = Displacement(insertCenter - centerMm)  *  ScaleAbout(centerMm, s)  *  p
        private static Matrix3d BuildMasterToSketchTransform(Point3d centerMm, Point3d insertCenter, double unitsPerMetre)
        {
            // Scale about the master centre
            var scaleAboutCenter = Matrix3d.Scaling(unitsPerMetre, centerMm);

            // IMPORTANT: displacement is (insertCenter - centerMm), NOT (insertCenter - s*centerMm)
            var postShift = Matrix3d.Displacement(insertCenter - centerMm);

            // Apply scale first, then shift
            return postShift * scaleAboutCenter;
        }

        private struct VertexIndexRecord
        {
            public CoordinateZone zone;
            public string sec, twp, rge, mer;
            public (double MinX, double MinY, double MaxX, double MaxY) aabb;
            public List<Point3d> verts;
            public bool closed;
        }

        private bool TryReadSectionFromJsonl(string jsonlPath, SectionKey key, out VertexIndexRecord rec)
        {
            rec = default;
            var ic = CultureInfo.InvariantCulture;

            using (var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!TryParseJsonLine(line, out var r)) continue;

                    if (r.zone == key.Zone &&
                        EqNorm(r.sec, key.Section) &&
                        EqNorm(r.twp, key.Township) &&
                        EqNorm(r.rge, key.Range) &&
                        EqNorm(r.mer, key.Meridian))
                    {
                        rec = r;
                        return true;
                    }
                }
            }
            return false;

            bool TryParseJsonLine(string s, out VertexIndexRecord r)
            {
                r = default;
                try
                {
                    string GetStr(string tag)
                    {
                        int idxTag = s.IndexOf($"\"{tag}\"", StringComparison.OrdinalIgnoreCase);
                        if (idxTag < 0) return null;
                        int colon = s.IndexOf(':', idxTag); if (colon < 0) return null;
                        int q1 = s.IndexOf('\"', colon + 1); if (q1 < 0) return null;
                        int q2 = s.IndexOf('\"', q1 + 1); if (q2 < 0) return null;
                        return s.Substring(q1 + 1, q2 - q1 - 1);
                    }

                    string sec = GetStr("SEC");
                    string twp = GetStr("TWP");
                    string rge = GetStr("RGE");
                    string mer = GetStr("MER");

                    string normSec = NormStr(sec);
                    string normTwp = NormStr(twp);
                    string normRge = NormStr(rge);
                    string normMer = NormStr(mer);

                    double zoneValue = ExtractNumberAfter(s, "\"ZONE\"");
                    CoordinateZone zone = CoordinateZone.Zone11;
                    if (TryConvertToZone((int)Math.Round(zoneValue), out var parsedZone))
                    {
                        zone = parsedZone;
                    }
                    else if (MeridianMatchesZone(CoordinateZone.Zone12, normMer))
                    {
                        zone = CoordinateZone.Zone12;
                    }

                    double minx = ExtractNumberAfter(s, "\"minx\"");
                    double miny = ExtractNumberAfter(s, "\"miny\"");
                    double maxx = ExtractNumberAfter(s, "\"maxx\"");
                    double maxy = ExtractNumberAfter(s, "\"maxy\"");

                    bool closed = s.IndexOf("\"Closed\":true", StringComparison.OrdinalIgnoreCase) >= 0;

                    int iv = s.IndexOf("\"Verts\":[", StringComparison.OrdinalIgnoreCase);
                    if (iv < 0) return false;
                    int start = s.IndexOf('[', iv + 8);
                    int end = s.LastIndexOf(']');
                    if (start < 0 || end < 0 || end <= start) return false;

                    var verts = new List<Point3d>();
                    int idx = start + 1;
                    while (idx < end)
                    {
                        int a = s.IndexOf('[', idx);
                        if (a < 0 || a > end) break;
                        int b = s.IndexOf(']', a);
                        if (b < 0 || b > end) break;
                        string pair = s.Substring(a + 1, b - a - 1);
                        int comma = pair.IndexOf(',');
                        if (comma > 0)
                        {
                            string sx = pair.Substring(0, comma).Trim();
                            string sy = pair.Substring(comma + 1).Trim();
                            if (double.TryParse(sx, NumberStyles.Float, ic, out double x) &&
                                double.TryParse(sy, NumberStyles.Float, ic, out double y))
                            {
                                verts.Add(new Point3d(x, y, 0));
                            }
                        }
                        idx = b + 1; // advance
                    }

                    r = new VertexIndexRecord
                    {
                        zone = zone,
                        sec = normSec,
                        twp = normTwp,
                        rge = normRge,
                        mer = normMer,
                        aabb = (minx, miny, maxx, maxy),
                        verts = verts,
                        closed = closed
                    };
                    return (verts.Count >= 3);
                }
                catch { return false; }
            }

            double ExtractNumberAfter(string s2, string tag)
            {
                int posTag = s2.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (posTag < 0) return 0;
                int colon = s2.IndexOf(':', posTag);
                if (colon < 0) return 0;
                int startNum = colon + 1;
                while (startNum < s2.Length && char.IsWhiteSpace(s2[startNum])) startNum++;
                int endNum = startNum;
                while (endNum < s2.Length &&
                       (char.IsDigit(s2[endNum]) || s2[endNum] == '.' || s2[endNum] == '-' ||
                        s2[endNum] == 'e' || s2[endNum] == 'E' || s2[endNum] == '+'))
                {
                    endNum++;
                }
                double v;
                if (double.TryParse(s2.Substring(startNum, endNum - startNum), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v))
                    return v;
                return 0;
            }
        }



        // -------- ObjectData read helpers & string normalizers --------

        private static string ReadOd(FieldDefinitions defs, Record rec, string[] aliases, Func<MapValue, string> toString)
        {
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def == null) continue;
                if (!aliases.Any(a => a.Equals(def.Name, StringComparison.OrdinalIgnoreCase))) continue;

                MapValue mv;
                try { mv = rec[i]; } catch { continue; }
                string s = toString(mv);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return null;
        }

        private string MapValueToString(MapValue mv)
        {
            if (mv == null) return null;
            switch (mv.Type)
            {
                case OdDataType.Character: return mv.StrValue;
                case OdDataType.Integer: return mv.Int32Value.ToString(CultureInfo.InvariantCulture);
                case OdDataType.Real: return mv.DoubleValue.ToString("0.####", CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        private static string NormStr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string trimmed = s.Trim();

            // Prefer numeric content even when prefixed (e.g., "SEC-23", "TWP-031", "MER-5")
            string digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(digitsOnly) && int.TryParse(digitsOnly, out int digitValue))
            {
                return digitValue.ToString(CultureInfo.InvariantCulture);
            }

            if (int.TryParse(trimmed, out int n)) return n.ToString(CultureInfo.InvariantCulture);
            string noZeros = trimmed.TrimStart('0');
            return noZeros.Length > 0 ? noZeros : "0";
        }

        private static bool EqNorm(string a, string b)
        {
            if (int.TryParse(a?.Trim(), out int ai) && int.TryParse(b?.Trim(), out int bi)) return ai == bi;
            string aa = NormStr(a);
            string bb = NormStr(b);
            if (int.TryParse(aa, out ai) && int.TryParse(bb, out bi)) return ai == bi;
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool MeridianMatchesZone(CoordinateZone zone, string merValue)
        {
            if (string.IsNullOrWhiteSpace(merValue)) return false;
            string normalized = NormStr(merValue);
            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mer)) return false;

            switch (zone)
            {
                case CoordinateZone.Zone11:
                    return mer == 5 || mer == 6;
                case CoordinateZone.Zone12:
                    return mer == 4;
                default:
                    return false;
            }
        }

        // --------- User prompts & master points reader ---------

        private bool PromptSectionKey(Editor ed, out SectionKey key)
        {
            key = default;

            if (!PromptZone(ed, out CoordinateZone zone)) return false;

            string sec = PromptString(ed, "Enter SEC: ");
            if (sec == null) return false;

            string twp = PromptString(ed, "Enter TWP: ");
            if (twp == null) return false;

            string rge = PromptString(ed, "Enter RGE: ");
            if (rge == null) return false;

            string mer = PromptString(ed, "Enter MER: ");
            if (mer == null) return false;

            key = new SectionKey(zone, sec, twp, rge, mer);
            return true;
        }

        private bool PromptZone(Editor ed, out CoordinateZone zone)
        {
            zone = default;

            var opts = new PromptKeywordOptions("\nEnter Zone [11/12]: ") { AllowNone = false };
            opts.Keywords.Add("11");
            opts.Keywords.Add("12");
            opts.Keywords.Default = "11";

            var res = ed.GetKeywords(opts);
            if (res.Status != PromptStatus.OK) return false;

            zone = (string.Equals(res.StringResult, "12", StringComparison.OrdinalIgnoreCase))
                ? CoordinateZone.Zone12
                : CoordinateZone.Zone11;
            return true;
        }

        private string PromptString(Editor ed, string message)
        {
            var opts = new PromptStringOptions("\n" + message) { AllowSpaces = false };
            var res = ed.GetString(opts);
            return (res.Status == PromptStatus.OK) ? res.StringResult : null;
        }
        // Prefer matching BLOCKs over DBPOINTs when both are within tol.
        private static int FindNearestIdxPreferBlocks(List<(ObjectId id, Point3d pos, bool isBlock)> list, Point3d target, double tol)
        {
            double tol2 = tol * tol;

            int bestIdx = -1;
            double bestD2 = double.MaxValue;

            // Pass 1: prefer blocks
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].isBlock) continue;
                double dx = target.X - list[i].pos.X;
                double dy = target.Y - list[i].pos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= tol2 && d2 < bestD2)
                {
                    bestD2 = d2; bestIdx = i;
                }
            }
            if (bestIdx >= 0) return bestIdx;

            // Pass 2: allow DBPOINTs if no blocks within tol
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].isBlock) continue;
                double dx = target.X - list[i].pos.X;
                double dy = target.Y - list[i].pos.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 <= tol2 && d2 < bestD2)
                {
                    bestD2 = d2; bestIdx = i;
                }
            }
            return bestIdx;
        }

        private List<Point3d> ReadPointsFromMaster(string masterPointsPath, out bool exists)
        {
            exists = File.Exists(masterPointsPath);
            if (!exists) return new List<Point3d>();

            var points = new List<Point3d>();
            using (var masterDb = new Database(false, true))
            {
                masterDb.ReadDwgFile(masterPointsPath, FileOpenMode.OpenForReadAndAllShare, false, null);
                masterDb.CloseInput(true);

                using (Transaction tr = masterDb.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(masterDb.BlockTableId, DbOpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], DbOpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        var ent = tr.GetObject(id, DbOpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        switch (ent)
                        {
                            case DBPoint dbPoint:
                                points.Add(dbPoint.Position);
                                break;
                            case BlockReference blockRef:
                                points.Add(blockRef.Position);
                                break;
                        }
                    }

                    tr.Commit();
                }
            }
            return points;
        }
        private static void HardFlushAndSaveMaster(Document masterDoc, string path, Editor ed)
        {
            try
            {
                // Light "touch" to ensure DB is considered dirty if needed
                masterDoc.Database.TileMode = masterDoc.Database.TileMode; // noop touch

                // Removed: masterDoc.Database.Caption (doesn't exist on Database)

                var dbPath = NormalizePath(masterDoc.Database.Filename);
                var targetPath = NormalizePath(path);

                if (!string.IsNullOrWhiteSpace(targetPath) &&
                    string.Equals(dbPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Open doc points at the same file; SaveAs to the same path to force a flush
                    masterDoc.Database.SaveAs(targetPath, DwgVersion.Current);
                    ed?.WriteMessage($"\n[Save] Database.SaveAs â†’ {targetPath}");
                }
                else
                {
                    // Fallback: close & save to the requested path
                    masterDoc.CloseAndSave(string.IsNullOrWhiteSpace(path) ? masterDoc.Database.Filename : path);
                    ed?.WriteMessage($"\n[Save] CloseAndSave â†’ {path}");
                }
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\n[Save] Failed: {ex.Message}");
                throw;
            }
        }

        // --------- Geometry & utilities ---------

        // Robustly find TOP-LEFT (NW) and TOP-RIGHT (NE) corners of the master section.
        // Works even when the top edge has a slight slope and vertex spacing is irregular.
        // Robustly find TOP-LEFT (NW) and TOP-RIGHT (NE) corners of the master section.
        // Works even when the top edge has a slight slope and vertex spacing is irregular.
        private bool TryGetSectionTopCorners(List<Point3d> verts, out Point3d topLeft, out Point3d topRight)
        {
            topLeft = topRight = Point3d.Origin;
            if (verts == null || verts.Count < 3) return false;

            // Build edges
            int n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (int i = 0; i < n; i++)
            {
                Point3d a = verts[i];
                Point3d b = verts[(i + 1) % n];
                Vector3d v = b - a;
                double len = v.Length;
                if (len <= 1e-9) continue;
                Vector3d u = new Vector3d(v.X / len, v.Y / len, 0);
                Point3d mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }
            if (edges.Count == 0) return false;

            // Choose a near-horizontal edge with highest average Y; fallback = longest edge.
            const double degTol = 15.0;
            double cosTol = Math.Cos(degTol * Math.PI / 180.0);
            EdgeInfo topEdge = default(EdgeInfo);
            double bestTopY = double.MinValue;
            foreach (var e in edges)
            {
                double horiz = Math.Abs(e.U.DotProduct(Vector3d.XAxis));
                double avgY = (e.A.Y + e.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY) { bestTopY = avgY; topEdge = e; }
            }
            if (bestTopY == double.MinValue)
                topEdge = edges.OrderByDescending(e => e.Len).First();

            // Local axes: EAST along the top edge, NORTH perpendicular
            Vector3d east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12) return false;
            Vector3d north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            // Extents & band tolerance (2 m or 0.5% of the larger span)
            double minE = double.MaxValue, maxE = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (var v in verts)
            {
                double pe = AxisProj(v, east);
                double pn = AxisProj(v, north);
                if (pe < minE) minE = pe; if (pe > maxE) maxE = pe;
                if (pn < minN) minN = pn; if (pn > maxN) maxN = pn;
            }
            double spanE = Math.Max(1e-6, maxE - minE);
            double spanN = Math.Max(1e-6, maxN - minN);
            double bandTol = Math.Max(2.0, 0.005 * Math.Max(spanE, spanN));

            // Build east-west chains and pick the one touching the top band
            var eChains = BuildChainsClosest(edges, east, north);
            if (eChains.Count == 0) return false;

            bool TouchesTop(ChainInfo ch)
            {
                foreach (int idx in ChainVertexIndices(ch, n))
                    if (maxN - AxisProj(verts[idx], north) <= bandTol)
                        return true;
                return false;
            }

            ChainInfo topChain = eChains
                .Where(TouchesTop)
                .OrderByDescending(c => c.Score)       // most "northern"
                .ThenByDescending(c => c.TotalLen)     // tie-break by length
                .DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).ThenByDescending(c => c.TotalLen).First())
                .First();

            // The corners are the min/max EAST vertices along that top chain
            int leftIdx = topChain.Start % n, rightIdx = leftIdx;
            double bestLeftE = double.MaxValue, bestRightE = double.MinValue;
            foreach (int idx in ChainVertexIndices(topChain, n))
            {
                double pe = AxisProj(verts[idx], east);
                if (pe < bestLeftE) { bestLeftE = pe; leftIdx = idx; }
                if (pe > bestRightE) { bestRightE = pe; rightIdx = idx; }
            }

            topLeft = verts[leftIdx];
            topRight = verts[rightIdx];
            return true;
        }

        private static Point3d TransformScaledPoint(Point3d source, Point3d localOrigin, Point3d masterOrigin, double scale, double rotation)
        {
            double dx = source.X - localOrigin.X;
            double dy = source.Y - localOrigin.Y;

            double cos = Math.Cos(rotation);
            double sin = Math.Sin(rotation);

            double mx = masterOrigin.X + (dx * cos - dy * sin) * scale;
            double my = masterOrigin.Y + (dx * sin + dy * cos) * scale;

            return new Point3d(mx, my, 0);
        }

        // Simple ray-cast point-in-polygon (2D) â€” correct even/odd test
        private static bool PointInPolygon2D(List<Point3d> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;

                // Only consider edges that straddle the horizontal ray
                if ((yi > y) != (yj > y))
                {
                    // Compute the x coordinate of the intersection
                    double xint = xi + (y - yi) * (xj - xi) / (yj - yi);
                    if (x < xint) inside = !inside;
                }
            }
            return inside;
        }

        private static int FindNearIndex(List<Point3d> pts, Point3d p, double tol)
        {
            double tol2 = tol * tol;
            for (int i = 0; i < pts.Count; i++)
            {
                var q = pts[i];
                double dx = p.X - q.X, dy = p.Y - q.Y;
                if (dx * dx + dy * dy <= tol2) return i;
            }
            return -1;
        }

        private static bool HasNear(List<Point3d> pts, Point3d p, double tol)
            => FindNearIndex(pts, p, tol) >= 0;

        private static List<Point3d> DeduplicateList(List<Point3d> pts, double tol)
        {
            var outList = new List<Point3d>(pts.Count);
            foreach (var p in pts)
                if (!HasNear(outList, p, tol)) outList.Add(p);
            return outList;
        }
        // Set a single block attribute (if present) to a value.
        private static void SetBlockAttribute(BlockReference br, string tag, string value)
        {
            if (br == null || string.IsNullOrWhiteSpace(tag)) return;
            var tm = br.Database?.TransactionManager;
            if (tm == null) return;

            foreach (ObjectId attId in br.AttributeCollection)
            {
                var ar = tm.GetObject(attId, DbOpenMode.ForWrite, false) as AttributeReference;
                if (ar == null) continue;
                if (ar.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    ar.TextString = value ?? string.Empty;
                    // no break on purpose: some blocks might have duplicate tags we want consistent
                }
            }
        }

        // ---------------- value types ----------------

        private readonly struct SectionKey
        {
            public SectionKey(CoordinateZone zone, string sec, string twp, string rge, string mer)
            {
                Zone = zone;
                Section = (sec ?? string.Empty).Trim();
                Township = (twp ?? string.Empty).Trim();
                Range = (rge ?? string.Empty).Trim();
                Meridian = (mer ?? string.Empty).Trim();
            }

            public CoordinateZone Zone { get; }
            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }

            public override string ToString()
                => $"Zone {FormatZoneNumber(Zone)}, SEC {Section}, TWP {Township}, RGE {Range}, MER {Meridian}";
        }

        private readonly struct Aabb2d
        {
            public Aabb2d(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\MacroBuilder.cs
/////////////////////////////////////////////////////////////////////

using System;

namespace ResidenceSync.UI
{
    internal static class MacroBuilder
    {
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return cleaned.Trim();
        }

        private static string AppendIfPresent(string macro, string value)
        {
            var sanitized = Sanitize(value);
            if (!string.IsNullOrEmpty(sanitized))
            {
                macro += sanitized + "\n";
            }

            return macro;
        }

        public static string BuildBuildSec(string zone, string sec, string twp, string rge, string mer)
        {
            var macro = "BUILDSEC\n";
            // BUILDSEC first prompts for UTM confirmation; default to "Yes" so UI macros align with prompt order.
            macro = AppendIfPresent(macro, "Yes");
            macro = AppendIfPresent(macro, zone);
            macro = AppendIfPresent(macro, sec);
            macro = AppendIfPresent(macro, twp);
            macro = AppendIfPresent(macro, rge);
            macro = AppendIfPresent(macro, mer);
            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }

        public static string BuildPushResS(string zone)
        {
            var macro = "PUSHRESS\n";
            macro = AppendIfPresent(macro, zone);
            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }

        public static string BuildSurfDev(
            string zone,
            string sec,
            string twp,
            string rge,
            string mer,
            string size,
            string scale,
            bool? isSurveyed,
            bool? insertResidences)
        {
            var macro = "SURFDEV\n";
            macro = AppendIfPresent(macro, zone);
            macro = AppendIfPresent(macro, sec);
            macro = AppendIfPresent(macro, twp);
            macro = AppendIfPresent(macro, rge);
            macro = AppendIfPresent(macro, mer);
            // SURFDEV now prompts for grid size before scale.
            macro = AppendIfPresent(macro, size);
            macro = AppendIfPresent(macro, scale);

            if (isSurveyed.HasValue)
            {
                macro += (isSurveyed.Value ? "Surveyed" : "Unsurveyed") + "\n";
            }

            if (insertResidences.HasValue)
            {
                macro += (insertResidences.Value ? "Yes" : "No") + "\n";
            }

            return macro.EndsWith("\n", StringComparison.Ordinal) ? macro : macro + "\n";
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\RSPanel.cs
/////////////////////////////////////////////////////////////////////

using Autodesk.AutoCAD.ApplicationServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ResidenceSync.Properties;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ResidenceSync.UI
{
    public partial class RSPanel : UserControl
    {
        public RSPanel()
        {
            InitializeComponent();
            NormalizeTableChildren(); // ensure no child forces row growth
            InitializeZoneOptions();
            InitializeGridSizeOptions();
            InitializeScaleOptions();
            InitializeSurveyedOptions();
            InitializeInsertResidencesOptions();
            LoadUserSettings();
            InitializeQuickInsertButtons();
        }

        private void RSPanel_Load(object sender, EventArgs e)
        {
            // keep status row collapsed unless you set text/Visible=true later
            if (this.tableLayoutMain.RowStyles.Count >= 3)
            {
                this.tableLayoutMain.RowStyles[2].SizeType = System.Windows.Forms.SizeType.Absolute;
                this.tableLayoutMain.RowStyles[2].Height = 0F;
            }
        }

        private void NormalizeTableChildren()
        {
            foreach (Control c in tableSurfDev.Controls)
            {
                c.AutoSize = false;
                c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; // not Bottom
                c.Margin = new Padding(3);
                if (c is TextBox tb) tb.Multiline = false;
            }
        }


        // RSPanel.cs â€“ prompt user before sending macro
        private void btnBuildSection_Click(object sender, EventArgs e)
        {
            SaveUserSettings();

            // Confirm whether the user is in UTM
            var result = MessageBox.Show(
                "Are you in UTM?",
                "Confirm UTM",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            // If the user clicks â€œNoâ€, cancel the operation
            if (result != DialogResult.Yes)
            {
                SetStatus("Section build cancelled â€“ not in UTM.");
                return;
            }

            // User confirmed theyâ€™re in UTM â€“ proceed with macro
            var macro = MacroBuilder.BuildBuildSec(
                GetZoneSelectionValue(),
                GetTextValue(textSection),
                GetTextValue(textTownship),
                GetTextValue(textRange),
                GetTextValue(textMeridian));

            SendMacro(macro);
        }

        private void btnPushResidences_Click(object sender, EventArgs e)
        {
            SaveUserSettings();
            var macro = MacroBuilder.BuildPushResS(GetZoneSelectionValue());
            SendMacro(macro);
        }

        private void btnBuildSurface_Click(object sender, EventArgs e)
        {
            SaveUserSettings();
            var macro = MacroBuilder.BuildSurfDev(
                GetZoneSelectionValue(),
                GetTextValue(textSection),
                GetTextValue(textTownship),
                GetTextValue(textRange),
                GetTextValue(textMeridian),
                GetSurfaceSizeSelection(),
                GetScaleSelection(),
                GetSurveyedSelection(),
                GetInsertResidencesSelection());
            SendMacro(macro);
        }

        private void SendMacro(string macro)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    SetStatus("No active document.");
                    return;
                }

                doc.SendStringToExecute(macro, true, false, false);
                SetStatus($"Sent: {macro.Replace("\n", "\\n")}");
            }
            catch (System.Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
            }
        }

        private void SetStatus(string message)
        {
            lblStatus.Text = message;
        }

        private string GetSurfaceSizeSelection()
        {
            if (comboGridSize.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return "5x5";
        }

        private string GetScaleSelection()
        {
            if (comboScale.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            return null;
        }

        private bool? GetSurveyedSelection()
        {
            if (comboSurveyed.SelectedItem is string selected)
            {
                return string.Equals(selected, "Surveyed", StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }

        private bool? GetInsertResidencesSelection()
        {
            if (comboInsertResidences.SelectedItem is string selected)
            {
                return string.Equals(selected, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }

        private string GetTextValue(TextBox textBox)
        {
            var value = textBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private void LoadUserSettings()
        {
            var settings = Settings.Default;
            SelectZoneValue(settings.SectionKeyZone ?? "11");
            textSection.Text = settings.SectionKeySec ?? string.Empty;
            textTownship.Text = settings.SectionKeyTwp ?? string.Empty;
            textRange.Text = settings.SectionKeyRge ?? string.Empty;
            textMeridian.Text = settings.SectionKeyMer ?? string.Empty;

            var savedSize = settings.SurfDevSize;
            var gridIndex = -1;
            if (!string.IsNullOrWhiteSpace(savedSize))
            {
                gridIndex = comboGridSize.FindStringExact(savedSize);
            }

            if (gridIndex >= 0)
            {
                comboGridSize.SelectedIndex = gridIndex;
            }
            else
            {
                comboGridSize.SelectedIndex = comboGridSize.FindStringExact("5x5");
            }

            var savedScale = settings.SurfDevScale;
            if (!string.IsNullOrWhiteSpace(savedScale))
            {
                SelectComboValue(comboScale, savedScale);
            }
            else
            {
                SelectComboValue(comboScale, null);
            }

            var savedSurveyed = settings.SurfDevSurveyed;
            if (savedSurveyed.HasValue)
            {
                SelectComboValue(comboSurveyed, savedSurveyed.Value ? "Surveyed" : "Unsurveyed");
            }
            else
            {
                SelectComboValue(comboSurveyed, null);
            }

            var savedInsert = settings.SurfDevInsertResidences;
            if (savedInsert.HasValue)
            {
                SelectComboValue(comboInsertResidences, savedInsert.Value ? "Yes" : "No");
            }
            else
            {
                SelectComboValue(comboInsertResidences, "No");
            }
        }

        private void SaveUserSettings()
        {
            var settings = Settings.Default;
            settings.SectionKeyZone = GetZoneSelectionValue() ?? "11";
            settings.SectionKeySec = textSection.Text?.Trim();
            settings.SectionKeyTwp = textTownship.Text?.Trim();
            settings.SectionKeyRge = textRange.Text?.Trim();
            settings.SectionKeyMer = textMeridian.Text?.Trim();
            settings.SurfDevSize = GetSurfaceSizeSelection();
            settings.SurfDevScale = GetScaleSelection() ?? string.Empty;
            settings.SurfDevSurveyed = GetSurveyedSelection();
            settings.SurfDevInsertResidences = GetInsertResidencesSelection();
            settings.Save();
        }

        private void InitializeGridSizeOptions()
        {
            comboGridSize.Items.Clear();
            comboGridSize.Items.AddRange(new object[]
            {
                "3x3",
                "5x5",
                "7x7",
                "9x9"
            });

            if (comboGridSize.SelectedIndex < 0)
            {
                comboGridSize.SelectedIndex = comboGridSize.FindStringExact("5x5");
            }
        }

        private void InitializeScaleOptions()
        {
            comboScale.Items.Clear();
            comboScale.Items.AddRange(new object[]
            {
                "50k",
                "30k",
                "25k",
                "20k"
            });

            SelectComboValue(comboScale, "50k");
        }

        private void InitializeSurveyedOptions()
        {
            comboSurveyed.Items.Clear();
            comboSurveyed.Items.AddRange(new object[]
            {
                "Surveyed",
                "Unsurveyed"
            });

            SelectComboValue(comboSurveyed, "Surveyed");
        }

        private void InitializeInsertResidencesOptions()
        {
            comboInsertResidences.Items.Clear();
            comboInsertResidences.Items.AddRange(new object[]
            {
                "Yes",
                "No"
            });

            SelectComboValue(comboInsertResidences, "No");
        }

        private void InitializeZoneOptions()
        {
            comboZone.Items.Clear();
            comboZone.Items.Add(new ZoneOption("Zone 11", "11"));
            comboZone.Items.Add(new ZoneOption("Zone 12", "12"));

            if (comboZone.Items.Count > 0)
            {
                comboZone.SelectedIndex = 0;
            }
        }

        private void SelectComboValue(ComboBox comboBox, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }
                else
                {
                    comboBox.SelectedIndex = -1;
                }
                return;
            }

            var index = comboBox.FindStringExact(value);
            if (index >= 0)
            {
                comboBox.SelectedIndex = index;
            }
            else if (comboBox.Items.Count > 0 && comboBox.SelectedIndex < 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private void labelSurveyed_Click(object sender, EventArgs e)
        {

        }

        private void tableSurfDev_Paint(object sender, PaintEventArgs e)
        {

        }

        private string GetZoneSelectionValue()
        {
            if (comboZone.SelectedItem is ZoneOption option)
            {
                return option.Value;
            }

            if (comboZone.SelectedItem is string text)
            {
                return ExtractDigits(text);
            }

            return "11";
        }

        private void SelectZoneValue(string zoneValue)
        {
            if (string.IsNullOrWhiteSpace(zoneValue))
            {
                if (comboZone.Items.Count > 0)
                {
                    comboZone.SelectedIndex = 0;
                }
                return;
            }

            for (int i = 0; i < comboZone.Items.Count; i++)
            {
                if (comboZone.Items[i] is ZoneOption option &&
                    string.Equals(option.Value, zoneValue, StringComparison.OrdinalIgnoreCase))
                {
                    comboZone.SelectedIndex = i;
                    return;
                }
            }

            if (comboZone.Items.Count > 0)
            {
                comboZone.SelectedIndex = 0;
            }
        }

        private string ExtractDigits(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var digits = new string(input.Where(char.IsDigit).ToArray());
            return string.IsNullOrEmpty(digits) ? null : digits;
        }

        private sealed class ZoneOption
        {
            public ZoneOption(string display, string value)
            {
                Display = display;
                Value = value;
            }

            public string Display { get; }
            public string Value { get; }

            public override string ToString() => Display;
        }

        private ContextMenuStrip BuildMacroMenu((string label, string macro)[] macros)
        {
            var menu = new ContextMenuStrip();
            foreach (var (label, macro) in macros)
            {
                var item = new ToolStripMenuItem(label);
                var macroToRun = macro;
                item.Click += (sender, e) => RunInsertMacro(macroToRun);
                menu.Items.Add(item);
            }

            return menu;
        }

        private void AttachMenu(Button button, ContextMenuStrip menu)
        {
            button.Click += (sender, e) => menu.Show(button, new Point(0, button.Height));
        }

        private void RunInsertMacro(string macro)
        {
            SendMacro(macro + "\n");
        }

        private void InitializeQuickInsertButtons()
        {
            const string arrow = " â–¾";

            string BuildInsertMacro(string blockName) =>
                $"^C^C(progn (command \"-LAYER\" \"S\" \"0\" \"\") (InsertBlock1 \"{blockName}\"))";

            var btnFreeholdRadius = new Button
            {
                Name = "btnFreeholdRadius",
                Text = "Freehold Radius" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var freeholdMenu = BuildMacroMenu(new[]
            {
                ("Freehold Radius", BuildInsertMacro("blk_surf_dev_freehold"))
            });
            AttachMenu(btnFreeholdRadius, freeholdMenu);

            var btnExtentFabric = new Button
            {
                Name = "btnExtentFabric",
                Text = "Extent Fabric" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var extentMenu = BuildMacroMenu(new[]
            {
                ("50k Surveyed Ext.", BuildInsertMacro("50000_surv_fabric")),
                ("50k Unsurveyed Ext.", BuildInsertMacro("50000_ut_fabric"))
            });
            AttachMenu(btnExtentFabric, extentMenu);

            var btnTownshipFabric = new Button
            {
                Name = "btnTownshipFabric",
                Text = "Township Fabric" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var townshipMenu = BuildMacroMenu(new[]
            {
                ("50k Surveyed", BuildInsertMacro("fabric_twp50000")),
                ("20k Surveyed", BuildInsertMacro("fabric_twp20000")),
                ("25k Surveyed", BuildInsertMacro("fabric_twp25000")),
                ("30k Surveyed", BuildInsertMacro("fabric_twp30000"))
            });
            AttachMenu(btnTownshipFabric, townshipMenu);

            var btnRadiusCircles = new Button
            {
                Name = "btnRadiusCircles",
                Text = "Radius Circles" + arrow,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            var radiusMenu = BuildMacroMenu(new[]
            {
                ("50k Radius Circle", BuildInsertMacro("blk_50000_rad_circles")),
                ("20k Radius Circle", BuildInsertMacro("blk_20000_rad_circles")),
                ("25k Radius Circle", BuildInsertMacro("blk_25000_rad_circles")),
                ("30k Radius Circle", BuildInsertMacro("blk_30000_rad_circles"))
            });
            AttachMenu(btnRadiusCircles, radiusMenu);

            tableQuickButtons.Controls.Add(btnFreeholdRadius, 0, 0);
            tableQuickButtons.Controls.Add(btnExtentFabric, 1, 0);
            tableQuickButtons.Controls.Add(btnTownshipFabric, 2, 0);
            tableQuickButtons.Controls.Add(btnRadiusCircles, 3, 0);
        }
    }
}

/////////////////////////////////////////////////////////////////////
// FILE: C:\Users\Work Test 2\Desktop\SURF DEV\ResidenceSync\UI\RSUiCommands.cs
/////////////////////////////////////////////////////////////////////

// ResidenceSync UI Palette
// How to compile: Build the ResidenceSync solution in Visual Studio targeting .NET Framework 4.8.
// How to load: NETLOAD â†’ pick ResidenceSync.dll.
// How to open the UI: run command RSUI.
// Usage notes: The palette only sends command macros; any required picks or prompts continue in AutoCAD.

using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System.Drawing;
using System.Windows.Forms;

namespace ResidenceSync.UI
{
    public class RSUiCommands : IExtensionApplication
    {
        private static PaletteSet _paletteSet;
        private static RSPanel _panel;
        private static readonly object _syncRoot = new object();
        private static readonly System.Guid PaletteGuid = new System.Guid("2F0D4F71-7A8C-4C44-9F2C-A0A5D5C9E51E");

        [CommandMethod("ResidenceSync", "RSUI", CommandFlags.Modal)]
        public void ShowResidenceSyncPalette()
        {
            EnsurePalette();

            if (_paletteSet != null)
            {
                if (!_paletteSet.Visible)
                {
                    _paletteSet.Visible = true;
                }

                _paletteSet.KeepFocus = false;
                _paletteSet.Activate(0);
            }
        }

        private static void EnsurePalette()
        {
            if (_paletteSet != null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_paletteSet != null)
                {
                    return;
                }

                _panel = new RSPanel
                {
                    Dock = DockStyle.Fill
                };

                _paletteSet = new PaletteSet("ResidenceSync UI", PaletteGuid)
                {
                    Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowTabForSingle | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.Snappable,
                    Size = new Size(360, 520),
                    KeepFocus = false
                };

                _paletteSet.MinimumSize = new Size(320, 360);
                _paletteSet.DockEnabled = DockSides.Left | DockSides.Right | DockSides.Bottom;
                _paletteSet.EnableTransparency(false);
                _paletteSet.Add("ResidenceSync", _panel);
                _paletteSet.Visible = true;
            }
        }

        public void Initialize()
        {
        }

        public void Terminate()
        {
            if (_paletteSet != null)
            {
                _paletteSet.Visible = false;
                _paletteSet.Dispose();
                _paletteSet = null;
                _panel = null;
            }
        }
    }
}

