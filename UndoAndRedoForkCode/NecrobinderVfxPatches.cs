using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoAndRedoForkCode;

[HarmonyPatch(typeof(NNecrobinderVfx))]
internal static class NecrobinderVfxPatches
{
    [HarmonyPatch("UpdateFlameVisibility")]
    [HarmonyPrefix]
    private static bool UpdateFlameVisibilityPrefix(NNecrobinderVfx __instance, GodotObject animationState)
    {
        try
        {
            Node2D? head = ReflectionUtil.GetField<Node2D>(__instance, "_headRef");
            if (!IsValid(head) || !IsValid(animationState))
            {
                return false;
            }

            head!.Visible = new MegaAnimationState(animationState).GetCurrentAnimationName() != "die";
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            MainFile.Logger.Debug($"Ignored Necrobinder VFX visibility callback after restore: {ex.Message}");
        }

        return false;
    }

    [HarmonyPatch("OnScytheFlame1")]
    [HarmonyPrefix]
    private static bool OnScytheFlame1Prefix(NNecrobinderVfx __instance)
    {
        RestartParticlesSafely(__instance, "_scytheFireParticles1");
        return false;
    }

    [HarmonyPatch("OnScytheFlame2")]
    [HarmonyPrefix]
    private static bool OnScytheFlame2Prefix(NNecrobinderVfx __instance)
    {
        RestartParticlesSafely(__instance, "_scytheFireParticles2");
        return false;
    }

    private static void RestartParticlesSafely(NNecrobinderVfx instance, string fieldName)
    {
        try
        {
            GpuParticles2D? particles = ReflectionUtil.GetField<GpuParticles2D>(instance, fieldName);
            if (IsValid(particles))
            {
                particles!.Restart();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsValid(GodotObject? value)
    {
        return value != null && GodotObject.IsInstanceValid(value);
    }
}
