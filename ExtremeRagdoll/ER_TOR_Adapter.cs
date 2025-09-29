using System;
using System.Collections;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace ExtremeRagdoll
{
    /// Runtime adaptation of TOR TriggeredEffectTemplates:
    /// Set HasShockWave=true for damaging AOE effects (no XML edits).
    internal static class ER_TOR_Adapter
    {
        public static bool TryEnableShockwaves()
        {
            try
            {
                var teType = Type.GetType(
                    "TOR_Core.BattleMechanics.TriggeredEffect.TriggeredEffectTemplate, TOR_Core",
                    throwOnError: false
                );
                if (teType == null) return false;

                var mbObjMgr = MBObjectManager.Instance;
                var getListGeneric = typeof(MBObjectManager).GetMethod("GetObjectTypeList");
                var getList = getListGeneric.MakeGenericMethod(teType);
                var list = (IEnumerable)getList.Invoke(mbObjMgr, null);
                if (list == null) return false;

                int changed = 0;

                var pHasShock = teType.GetProperty("HasShockWave");
                var pDmg = teType.GetProperty("DamageAmount");
                var pRad = teType.GetProperty("Radius");
                var pDmgType = teType.GetProperty("DamageType");
                var pTarget = teType.GetProperty("TargetType");

                foreach (var te in list)
                {
                    bool hasShock = (bool)(pHasShock?.GetValue(te) ?? false);
                    float dmg = pDmg != null ? Convert.ToSingle(pDmg.GetValue(te)) : 0f;
                    float rad = pRad != null ? Convert.ToSingle(pRad.GetValue(te)) : 0f;
                    string target = pTarget?.GetValue(te)?.ToString() ?? "";
                    string dmgType = pDmgType?.GetValue(te)?.ToString() ?? "";

                    // Heuristic: only enable on actual damaging AOEs that are not friendly/self.
                    bool isDamaging = dmg > 0f && !string.Equals(dmgType, "Invalid", StringComparison.OrdinalIgnoreCase);
                    bool isAOE = rad >= 2f;
                    bool affectsHostiles = !(target.Equals("Friendly", StringComparison.OrdinalIgnoreCase)
                                           || target.Equals("Self", StringComparison.OrdinalIgnoreCase));

                    if (!hasShock && isDamaging && isAOE && affectsHostiles)
                    {
                        // Prefer property; fallback to private field if needed.
                        if (pHasShock != null && pHasShock.CanWrite)
                        {
                            pHasShock.SetValue(te, true);
                            changed++;
                        }
                        else
                        {
                            var fHasShock = teType.GetField("<HasShockWave>k__BackingField",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                                ?? teType.GetField("_hasShockWave", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (fHasShock != null)
                            {
                                fHasShock.SetValue(te, true);
                                changed++;
                            }
                        }
                    }
                }

                // Return true if we touched anything.
                ER_Log.Info($"TOR: enabled shockwaves on {changed} effects");
                Debug.Print($"[ExtremeRagdoll] Enabled shockwaves on {changed} TOR effects");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
