using System;
using Bestiary.monsters;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.map.entities;
using ProjectMage.player;
using ProjectMage.player.menu;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // DRAW ALL PER-PLAYER UI (coop markers, nameplates, prompts)
    private static void DrawAllPerPlayerUi()
    {
        var p1 = MainPlayer();
        var p2 = CoopPlayer();
        if (p1 == null || p2 == null) return;

        var screenW = (int)ScrollManager.screenSize.X;
        var screenH = (int)ScrollManager.screenSize.Y;
        var halfW = screenW / 2;
        var cropX = halfW / 2;

        var savedScroll = GetScroll();

        SpriteTools.BeginAlpha();

        // Player 1 (left half)
        SetScroll(HalfScroll(0));
        DrawCoopMarker(p1, new Rectangle(0, 0, halfW, screenH), cropX);
        DrawNameplate(p1, new Rectangle(0, 0, halfW, screenH), cropX);
        DrawInteractionPrompt(p1, new Rectangle(0, 0, halfW, screenH), cropX);

        // Player 2 (right half)
        SetScroll(HalfScroll(1));
        DrawCoopMarker(p2, new Rectangle(halfW, 0, halfW, screenH), cropX);
        DrawNameplate(p2, new Rectangle(halfW, 0, halfW, screenH), cropX);
        DrawInteractionPrompt(p2, new Rectangle(halfW, 0, halfW, screenH), cropX);

        SetScroll(savedScroll);
        SpriteTools.End();
    }

    // COOP MARKER (arrow above player, on-screen or edge)
    private static void DrawCoopMarker(Player player, Rectangle halfRect, int cropX)
    {
        if (ConfigMgr.playerMarkers == 0) return;
        if (!((bool)IsLocalCoopModeMethod.Invoke(null, null) || player.isLocal)) return;

        var c = GetCharacter(player);
        if (c == null) return;

        float screenH = halfRect.Height;
        var markerScale = halfRect.Width / 540f;
        var edgeScale = screenH / 1080f;
        var num3 = screenH / ScrollManager.screenSize.Y;
        var screenCenter = new Vector2(halfRect.X + halfRect.Width / 2f,
            halfRect.Y + halfRect.Height / 2f);
        var isOther = player != MainPlayer();

        // Edge check (uses character.loc per vanilla)
        var edgeWorldPos = c.loc + new Vector2(0f, -70f);
        var edgeScreenPos = num3 * ScrollManager.GetScreenLoc(edgeWorldPos, 0);
        var edgeMappedX = edgeScreenPos.X - cropX;
        var edgeCheckPos = new Vector2(halfRect.X + edgeMappedX, edgeScreenPos.Y);

        var edgeMargin = 120f * edgeScale;
        var isOffScreen = edgeCheckPos.X < halfRect.X + edgeMargin
                          || edgeCheckPos.X > halfRect.X + halfRect.Width - edgeMargin
                          || edgeCheckPos.Y < halfRect.Y + edgeMargin
                          || edgeCheckPos.Y > halfRect.Y + halfRect.Height - edgeMargin;

        // On-screen position (uses markerDrawLoc per vanilla)
        var markerWorldPos = player.markerDrawLoc + new Vector2(0f, -140f);
        var markerScreenPos = num3 * ScrollManager.GetScreenLoc(markerWorldPos, 0);
        var mappedX = markerScreenPos.X - cropX;
        var markerPos = new Vector2(halfRect.X + mappedX, markerScreenPos.Y);

        TryInitIcc();
        Color markerColor;
        if (_iccColorReadSucceeded)
            try
            {
                markerColor = isOther
                    ? (Color)_iccCoopColorProp.GetValue(null)
                    : (Color)_iccMainColorProp.GetValue(null);
            }
            catch
            {
                _iccColorReadSucceeded = false;
                markerColor = isOther
                    ? new Color(1f, 0.5f, 0.4f, 1f)
                    : new Color(0.4f, 0.5f, 1f, 1f);
            }
        else
            markerColor = isOther
                ? new Color(1f, 0.5f, 0.4f, 1f)
                : new Color(0.4f, 0.5f, 1f, 1f);

        if (isOffScreen)
        {
            var dir = markerPos - screenCenter;
            var angle = (float)Math.Atan2(dir.Y, dir.X);
            var maxDistX = halfRect.Width / 2f - edgeMargin;
            var maxDistY = halfRect.Height / 2f - edgeMargin;

            if (Math.Abs(dir.X) > maxDistX)
                dir *= maxDistX / Math.Abs(dir.X);
            if (Math.Abs(dir.Y) > maxDistY)
                dir *= maxDistY / Math.Abs(dir.Y);

            var edgePos = screenCenter + dir;
            SpriteTools.sprite.Draw(UIRender.interfaceTex, edgePos,
                new Rectangle(128, 512, 64, 64), markerColor, angle,
                new Vector2(32f, 32f), edgeScale * 0.75f, SpriteEffects.None, 0f);

            if (!(c.dyingFrame > 0f)) return;

            var skullOff = new Vector2((float)(8.5 + 6.5 * Math.Cos(angle + Math.PI)),
                (float)(6.0 + 4.0 * Math.Sin(angle + Math.PI)));
            SpriteTools.sprite.Draw(UIRender.interfaceTex, edgePos - skullOff, UIRender.GetIconRect(60), Color.Black,
                0f, new Vector2(32f, 32f), edgeScale * 0.25f, SpriteEffects.None, 0f);
        }
        else
        {
            SpriteTools.sprite.Draw(UIRender.interfaceTex, markerPos, new Rectangle(1792, 834, 128, 126), markerColor,
                0f, new Vector2(64f, 128f), markerScale * 0.6f, SpriteEffects.None, 0f);

            if (!(c.dyingFrame > 0f)) return;
            var skullPos = markerPos + new Vector2(-10f, -42f) * markerScale;
            SpriteTools.sprite.Draw(UIRender.interfaceTex, skullPos,
                UIRender.GetIconRect(60), Color.Black, 0f,
                new Vector2(32f, 32f), markerScale * 0.25f, SpriteEffects.None, 0f);
        }
    }

    // NAMEPLATE (other player's health + name)
    private static void DrawNameplate(Player player, Rectangle halfRect, int cropX)
    {
        if (player.isLocal) return;

        var c = GetCharacter(player);
        if (c == null || c.monsterIdx < 0) return;

        float screenH = halfRect.Height;
        var scale = screenH / 1080f;
        var num3 = screenH / ScrollManager.screenSize.Y;

        var worldPos = player.namePlateDrawLoc + new Vector2(0f, -170f);
        var screenPos = num3 * ScrollManager.GetScreenLoc(worldPos, 0);
        var mappedX = screenPos.X - cropX;

        var isVisible = mappedX >= 0 && mappedX <= halfRect.Width;
        if (!isVisible) return;

        var drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
        var hpAlpha = GetPlayerHpBarFrame(player) / 2f;

        var name = player.nameStr;
        var rawColor = PlayerFaction.GetFactionColor(player.faction.GetFaction());
        var brightColor = rawColor / 4f + new Vector3(0.75f, 0.75f, 0.75f);
        var textColor = new Color(brightColor.X, brightColor.Y, brightColor.Z, 1f);

        Text.DrawText(name, drawPos + new Vector2(-2f, 2f), new Color(0f, 0f, 0f, 0.5f), 0.6f * scale, 1);
        Text.DrawText(name, drawPos, textColor, 0.6f * scale, 1);

        var barPos = player.namePlateDrawLoc + new Vector2(0f, -140f);
        var barScreen = num3 * ScrollManager.GetScreenLoc(barPos, 0);
        var barMappedX = barScreen.X - cropX;

        if (!(barMappedX >= 0) || !(barMappedX <= halfRect.Width)) return;
        var drawBarPos = new Vector2(halfRect.X + barMappedX, barScreen.Y);
        const int barWidth = 128;
        const int barHeight = 8;
        SpriteTools.sprite.Draw(UIRender.interfaceTex,
            new Rectangle((int)drawBarPos.X - barWidth / 2, (int)drawBarPos.Y - barHeight / 2, barWidth, barHeight),
            new Rectangle(130, 2, 28, 28), new Color(0f, 0f, 0f, 0.5f * hpAlpha));

        var healthPercent = player.networkHp / 65535f;
        var fillWidth = (int)(barWidth * healthPercent);
        SpriteTools.sprite.Draw(UIRender.interfaceTex,
            new Rectangle((int)drawBarPos.X - barWidth / 2, (int)drawBarPos.Y - barHeight / 2, fillWidth, barHeight),
            new Rectangle(130, 2, 28, 28), new Color(1f, 0f, 0f, hpAlpha));
    }

    // INTERACTION PROMPT (press A to talk, etc.)
    private static void DrawInteractionPrompt(Player player, Rectangle halfRect, int cropX)
    {
        if (player.prompts.drawActive)
        {
            var dv = player.prompts.drawVec;
            var screenPos = ScrollManager.GetScreenLoc(dv, 0);
            var mappedX = screenPos.X - cropX;
            if (!(mappedX >= 0) || !(mappedX <= halfRect.Width)) return;

            var drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
            var scale = halfRect.Height / 1080f * 0.6f;
            var interactText = player.prompts.InteractString();
            if (interactText is { Length: > 0 }) Text.DrawText(interactText, drawPos, Color.White, scale, 1, player, 0);

            return;
        }

        // Fallback: characters sometimes aren't picked up by vanilla GetCharUse in splitscreen, due to activeChars mismatch or timing.
        // We draw the prompt manually for nearby interactable(s) that vanilla missed.
        var pc = GetCharacter(player);
        if (pc == null || pc.dyingFrame > 0f) return;
        if (player.dialog.active) return;

        foreach (var c in CharMgr.character)
        {
            if (!c.exists || c.ID == pc.ID) continue;

            var monsterDef = MonsterCatalog.monsterDef[c.monsterIdx];
            if (monsterDef == null) continue;

            var canInteract = false;
            switch (monsterDef.type)
            {
                case 0:
                    if ((c.canConsume && c.dyingFrame > 0f && c.IsMage()) || (c.IsPlayer() && c.dyingFrame > 1f) ||
                        (c.anim.canInteract && c.anim.animName != "downed"))
                        canInteract = true;
                    break;
                case 2:
                    if (c.anim.animName == "chest")
                        canInteract = true;
                    break;
                case 3:
                    if (c.anim.animName == "switch" || c.anim.animName == "checkpoint")
                        canInteract = true;
                    break;
                case 5:
                    if (c.anim.animName == "material")
                        canInteract = true;
                    break;
                case 6:
                    if (c.anim.canInteract)
                        canInteract = true;
                    break;
                case 7:
                    if (c.anim.animName == "switch" || c.anim.animName == "activated")
                        canInteract = true;
                    break;
            }

            if (!canInteract) continue;

            var rangeX = 200f + monsterDef.boxWidth / 2f;
            const float rangeY = 250f;
            if (Math.Abs(pc.loc.X - c.loc.X) > rangeX) continue;
            if (Math.Abs(pc.loc.Y - c.loc.Y) > rangeY) continue;

            var dv = new Vector2(c.loc.X, c.loc.Y - monsterDef.boxHeight);
            if (c.zipPairIdx > -1)
                dv.Y = c.loc.Y - monsterDef.boxHeight - 52f;

            var screenPos = ScrollManager.GetScreenLoc(dv, 0);
            var mappedX = screenPos.X - cropX;
            if (mappedX < 0 || mappedX > halfRect.Width) continue;

            var drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
            var scale = halfRect.Height / 1080f * 0.6f;
            var interactText = player.prompts.InteractString();
            if (interactText is { Length: > 0 })
                Text.DrawText(interactText, drawPos, Color.White, scale, 1, player, 0);
            return;
        }
    }

    // Harmony patches to suppress original drawing during capture
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "DrawMarkers", [])]
    private static bool Player_DrawMarkers_Prefix()
    {
        return !ShouldSkipIndicators;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.DrawNameplate), [])]
    private static bool Player_DrawNameplate_Prefix()
    {
        return !ShouldSkipIndicators;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerPrompts), nameof(PlayerPrompts.Draw), [])]
    private static bool PlayerPrompts_Draw_Prefix()
    {
        return !ShouldSkipIndicators;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "DrawAiming", [])]
    private static bool Player_DrawAiming_Prefix()
    {
        return !ShouldSkipIndicators;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(PlayerMenu), "DrawCoopMarker")]
    private static bool PlayerMenu_DrawCoopMarker_Prefix()
    {
        return !ShouldSkipIndicators;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Grapple), "Draw")]
    private static bool Grapple_Draw_Prefix()
    {
        return !_skipGrapples;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerMgr), "Draw")]
    private static bool PlayerMgr_Draw_Prefix()
    {
        if (!SplitActive) return true;
        return !_skipGeneralHud;
    }
}