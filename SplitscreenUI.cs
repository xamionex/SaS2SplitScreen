using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Common;
using HarmonyLib;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.director;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.outro;
using ProjectMage.map.entities;
using ProjectMage.map.pickups;
using ProjectMage.particles;
using ProjectMage.gamestate.mage;
using ProjectMage.player;
using ProjectMage.player.menu;
using ProjectMage.texturesheet;
using SalmonMaps.director.bloom;
using SalmonMaps.map;
using Bestiary.monsters;

namespace SaS2SplitScreen;

internal static partial class SplitscreenPatch
{
    // DRAW ALL PER-PLAYER UI (coop markers, nameplates, prompts)
    private static void DrawAllPerPlayerUI()
    {
        var p1 = MainPlayer();
        var p2 = CoopPlayer();
        if (p1 == null || p2 == null) return;

        int screenW = (int)ScrollManager.screenSize.X;
        int screenH = (int)ScrollManager.screenSize.Y;
        int halfW = screenW / 2;
        int cropX = halfW / 2;

        Vector2 savedScroll = GetScroll();

        SpriteTools.BeginAlpha();

        // Player 1 (left half)
        SetScroll(ScrollFor(_p1Loc));
        DrawCoopMarker(p1, new Rectangle(0, 0, halfW, screenH), cropX);
        DrawNameplate(p1, new Rectangle(0, 0, halfW, screenH), cropX);
        DrawInteractionPrompt(p1, new Rectangle(0, 0, halfW, screenH), cropX);

        // Player 2 (right half)
        SetScroll(ScrollFor(_p2Loc));
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
        if (!((bool)_isLocalCoopModeMethod.Invoke(null, null) || player.isLocal)) return;

        Character c = GetCharacter(player);
        if (c == null) return;

        float screenH = halfRect.Height;
        float markerScale = halfRect.Width / 540f;
        float edgeScale = screenH / 1080f;
        float num3 = screenH / ScrollManager.screenSize.Y;
        Vector2 screenCenter = new Vector2(halfRect.X + halfRect.Width / 2f,
            halfRect.Y + halfRect.Height / 2f);
        bool isOther = player != MainPlayer();

        // Edge check (uses character.loc per vanilla)
        Vector2 edgeWorldPos = c.loc + new Vector2(0f, -70f);
        Vector2 edgeScreenPos = num3 * ScrollManager.GetScreenLoc(edgeWorldPos, 0);
        float edgeMappedX = edgeScreenPos.X - cropX;
        Vector2 edgeCheckPos = new Vector2(halfRect.X + edgeMappedX, edgeScreenPos.Y);

        float edgeMargin = 120f * edgeScale;
        bool isOffScreen = edgeCheckPos.X < halfRect.X + edgeMargin
                           || edgeCheckPos.X > halfRect.X + halfRect.Width - edgeMargin
                           || edgeCheckPos.Y < halfRect.Y + edgeMargin
                           || edgeCheckPos.Y > halfRect.Y + halfRect.Height - edgeMargin;

        // On-screen position (uses markerDrawLoc per vanilla)
        Vector2 markerWorldPos = player.markerDrawLoc + new Vector2(0f, -140f);
        Vector2 markerScreenPos = num3 * ScrollManager.GetScreenLoc(markerWorldPos, 0);
        float mappedX = markerScreenPos.X - cropX;
        Vector2 markerPos = new Vector2(halfRect.X + mappedX, markerScreenPos.Y);

        TryInitIcc();
        Color markerColor;
        if (_iccColorReadSucceeded)
        {
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
        }
        else
        {
            markerColor = isOther
                ? new Color(1f, 0.5f, 0.4f, 1f)
                : new Color(0.4f, 0.5f, 1f, 1f);
        }

        if (isOffScreen)
        {
            Vector2 dir = markerPos - screenCenter;
            float angle = (float)Math.Atan2(dir.Y, dir.X);
            float maxDistX = halfRect.Width / 2f - edgeMargin;
            float maxDistY = halfRect.Height / 2f - edgeMargin;

            if (Math.Abs(dir.X) > maxDistX)
                dir *= maxDistX / Math.Abs(dir.X);
            if (Math.Abs(dir.Y) > maxDistY)
                dir *= maxDistY / Math.Abs(dir.Y);

            Vector2 edgePos = screenCenter + dir;
            SpriteTools.sprite.Draw(UIRender.interfaceTex, edgePos,
                new Rectangle(128, 512, 64, 64), markerColor, angle,
                new Vector2(32f, 32f), edgeScale * 0.75f, SpriteEffects.None, 0f);

            if (c.dyingFrame > 0f)
            {
                Vector2 skullOff = new Vector2((float)(8.5 + 6.5 * Math.Cos(angle + Math.PI)),
                    (float)(6.0 + 4.0 * Math.Sin(angle + Math.PI)));
                SpriteTools.sprite.Draw(UIRender.interfaceTex,
                    edgePos - skullOff,
                    UIRender.GetIconRect(60), Color.Black, 0f,
                    new Vector2(32f, 32f), edgeScale * 0.25f, SpriteEffects.None, 0f);
            }
        }
        else
        {
            SpriteTools.sprite.Draw(UIRender.interfaceTex, markerPos,
                new Rectangle(1792, 834, 128, 126), markerColor, 0f,
                new Vector2(64f, 128f), markerScale * 0.6f, SpriteEffects.None, 0f);

            if (c.dyingFrame > 0f)
            {
                Vector2 skullPos = markerPos + new Vector2(-10f, -42f) * markerScale;
                SpriteTools.sprite.Draw(UIRender.interfaceTex, skullPos,
                    UIRender.GetIconRect(60), Color.Black, 0f,
                    new Vector2(32f, 32f), markerScale * 0.25f, SpriteEffects.None, 0f);
            }
        }
    }

    // NAMEPLATE (other player's health + name)
    private static void DrawNameplate(Player player, Rectangle halfRect, int cropX)
    {
        if (player.isLocal) return;

        Character c = GetCharacter(player);
        if (c == null || c.monsterIdx < 0) return;

        float screenH = halfRect.Height;
        float scale = screenH / 1080f;
        float num3 = screenH / ScrollManager.screenSize.Y;

        Vector2 worldPos = player.namePlateDrawLoc + new Vector2(0f, -170f);
        Vector2 screenPos = num3 * ScrollManager.GetScreenLoc(worldPos, 0);
        float mappedX = screenPos.X - cropX;

        bool isVisible = mappedX >= 0 && mappedX <= halfRect.Width;
        if (isVisible)
        {
            Vector2 drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
            float hpAlpha = GetPlayerHpBarFrame(player) / 2f;

            StringBuilder name = player.nameStr;
            Vector3 rawColor = PlayerFaction.GetFactionColor(player.faction.GetFaction());
            Vector3 brightColor = rawColor / 4f + new Vector3(0.75f, 0.75f, 0.75f);
            Color textColor = new Color(brightColor.X, brightColor.Y, brightColor.Z, 1f);

            Text.DrawText(name, drawPos + new Vector2(-2f, 2f), new Color(0f, 0f, 0f, 0.5f), 0.6f * scale, 1);
            Text.DrawText(name, drawPos, textColor, 0.6f * scale, 1);

            Vector2 barPos = player.namePlateDrawLoc + new Vector2(0f, -140f);
            Vector2 barScreen = num3 * ScrollManager.GetScreenLoc(barPos, 0);
            float barMappedX = barScreen.X - cropX;
            if (barMappedX >= 0 && barMappedX <= halfRect.Width)
            {
                Vector2 drawBarPos = new Vector2(halfRect.X + barMappedX, barScreen.Y);
                int barWidth = 128;
                int barHeight = 8;
                SpriteTools.sprite.Draw(UIRender.interfaceTex,
                    new Rectangle((int)drawBarPos.X - barWidth / 2, (int)drawBarPos.Y - barHeight / 2, barWidth,
                        barHeight),
                    new Rectangle(130, 2, 28, 28), new Color(0f, 0f, 0f, 0.5f * hpAlpha));

                float healthPercent = (float)player.networkHp / 65535f;
                int fillWidth = (int)(barWidth * healthPercent);
                SpriteTools.sprite.Draw(UIRender.interfaceTex,
                    new Rectangle((int)drawBarPos.X - barWidth / 2, (int)drawBarPos.Y - barHeight / 2, fillWidth,
                        barHeight),
                    new Rectangle(130, 2, 28, 28), new Color(1f, 0f, 0f, hpAlpha));
            }
        }
    }

    // INTERACTION PROMPT (press A to talk, etc.)
    private static void DrawInteractionPrompt(Player player, Rectangle halfRect, int cropX)
    {
        if (player.prompts.drawActive)
        {
            Vector2 dv = player.prompts.drawVec;
            Vector2 screenPos = ScrollManager.GetScreenLoc(dv, 0);
            float mappedX = screenPos.X - cropX;
            if (mappedX >= 0 && mappedX <= halfRect.Width)
            {
                Vector2 drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
                float scale = halfRect.Height / 1080f * 0.6f;
                StringBuilder interactText = player.prompts.InteractString();
                if (interactText != null && interactText.Length > 0)
                    Text.DrawText(interactText, drawPos, Color.White, scale, 1, player, 0);
            }
            return;
        }

        // Fallback: characters sometimes aren't picked up by vanilla GetCharUse in splitscreen,
        // due to activeChars mismatch or timing.
        // We draw the prompt manually for nearby interactables that vanilla missed.
        Character pc = GetCharacter(player);
        if (pc == null || pc.dyingFrame > 0f) return;
        if (player.dialog.active) return;

        for (int i = 0; i < CharMgr.character.Length; i++)
        {
            Character c = CharMgr.character[i];
            if (!c.exists || c.ID == pc.ID) continue;

            MonsterDef mdef = MonsterCatalog.monsterDef[c.monsterIdx];
            if (mdef == null) continue;

            bool canInteract = false;
            switch (mdef.type)
            {
                case 0:
                    if (c.canConsume && c.dyingFrame > 0f && c.IsMage())
                        canInteract = true;
                    else if (c.IsPlayer() && c.dyingFrame > 1f)
                        canInteract = true;
                    else if (c.anim.canInteract && c.anim.animName != "downed")
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

            float rangeX = 200f + mdef.boxWidth / 2f;
            float rangeY = 250f;
            if (Math.Abs(pc.loc.X - c.loc.X) > rangeX) continue;
            if (Math.Abs(pc.loc.Y - c.loc.Y) > rangeY) continue;

            Vector2 dv = new Vector2(c.loc.X, c.loc.Y - mdef.boxHeight);
            if (c.zipPairIdx > -1)
                dv.Y = c.loc.Y - mdef.boxHeight - 52f;

            Vector2 screenPos = ScrollManager.GetScreenLoc(dv, 0);
            float mappedX = screenPos.X - cropX;
            if (mappedX < 0 || mappedX > halfRect.Width) continue;

            Vector2 drawPos = new Vector2(halfRect.X + mappedX, screenPos.Y);
            float scale = halfRect.Height / 1080f * 0.6f;
            StringBuilder interactText = player.prompts.InteractString();
            if (interactText != null && interactText.Length > 0)
                Text.DrawText(interactText, drawPos, Color.White, scale, 1, player, 0);
            return;
        }
    }

    // Harmony patches to suppress original drawing during capture
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "DrawMarkers", new Type[0])]
    private static bool Player_DrawMarkers_Prefix() => !ShouldSkipIndicators;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.DrawNameplate), new Type[0])]
    private static bool Player_DrawNameplate_Prefix() => !ShouldSkipIndicators;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerPrompts), nameof(PlayerPrompts.Draw), new Type[0])]
    private static bool PlayerPrompts_Draw_Prefix() => !ShouldSkipIndicators;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "DrawAiming", new Type[0])]
    private static bool Player_DrawAiming_Prefix() => !ShouldSkipIndicators;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(PlayerMenu), "DrawCoopMarker")]
    private static bool PlayerMenu_DrawCoopMarker_Prefix() => !ShouldSkipIndicators;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Grapple), "Draw")]
    private static bool Grapple_Draw_Prefix() => !_skipGrapples;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerMgr), "Draw")]
    private static bool PlayerMgr_Draw_Prefix()
    {
        if (!SplitActive) return true;
        return !_skipGeneralHud;
    }

}
