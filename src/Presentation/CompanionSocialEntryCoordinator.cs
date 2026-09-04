using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;
using StardewValley.Menus;

namespace YuiToIssho;

internal sealed class CompanionSocialEntryCoordinator
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo? CurrentTabField = typeof(GameMenu).GetField("currentTab", InstanceFields);
    private static readonly FieldInfo? PagesField = typeof(GameMenu).GetField("pages", InstanceFields);
    private static readonly FieldInfo? SocialEntriesField = typeof(SocialPage).GetField("SocialEntries", InstanceFields);
    private static readonly FieldInfo? NumFarmersField = typeof(SocialPage).GetField("numFarmers", InstanceFields);
    private static readonly FieldInfo? CharacterSlotsField = typeof(SocialPage).GetField("characterSlots", InstanceFields);
    private static readonly FieldInfo? SlotPositionField = typeof(SocialPage).GetField("slotPosition", InstanceFields);
    private static readonly FieldInfo? ProfileCurrentField = typeof(ProfileMenu).GetField("Current", InstanceFields);
    private static readonly FieldInfo? ProfileDrawPositionField = typeof(ProfileMenu).GetField("_characterSpriteDrawPosition", InstanceFields);
    private static readonly FieldInfo? ProfileDirectionField = typeof(ProfileMenu).GetField("_currentDirection", InstanceFields);

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly CompanionSocialMenuCoordinator socialMenu;
    private readonly CompanionNativeMenuPortraitRenderer portraits = new();
    private readonly Dictionary<object, CompanionMenuIdentitySnapshot> injectedEntries = new(ReferenceEqualityComparer.Instance);
    private object? activePage;
    private object? disabledPage;
    private int originalFarmerCount;
    private bool warnedLayout;
    private Texture2D? transparentNpcSprite;

    public CompanionSocialEntryCoordinator(IModHelper helper, IMonitor monitor, CompanionSocialMenuCoordinator socialMenu)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.socialMenu = socialMenu;
    }

    public void Attach()
    {
        this.helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
        this.helper.Events.Display.MenuChanged += (_, _) => this.ResetPage();
        this.helper.Events.GameLoop.Saving += (_, _) => this.ResetPage();
        this.helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.ReleaseRuntime();
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (Game1.activeClickableMenu is ProfileMenu profile)
        {
            this.DrawProfilePortrait(profile, e.SpriteBatch);
            return;
        }
        if (Game1.activeClickableMenu is not GameMenu menu || !TryGetCurrentSocialPage(menu, out SocialPage? page))
            return;
        if (ReferenceEquals(this.disabledPage, page))
            return;

        try
        {
            if (!ReferenceEquals(this.activePage, page))
            {
                this.injectedEntries.Clear();
                this.activePage = page;
                this.originalFarmerCount = NumFarmersField?.GetValue(page) as int? ?? 0;
            }

            this.EnsureEntries(page);
            this.DrawSocialPortraits(page, e.SpriteBatch);
        }
        catch (Exception ex)
        {
            this.DisableAdapter(ex);
        }
    }

    private void EnsureEntries(SocialPage page)
    {
        if (SocialEntriesField?.GetValue(page) is not IList entries || NumFarmersField?.GetValue(page) is not int numFarmers)
            throw new MissingFieldException(typeof(SocialPage).FullName, "SocialEntries/numFarmers");

        IReadOnlyList<CompanionMenuIdentitySnapshot> identities = this.socialMenu.GetIdentityView();
        Type entryType = SocialEntriesField.FieldType.GetGenericArguments()[0];
        FieldInfo? internalNameField = entryType.GetField("InternalName", InstanceFields);
        if (internalNameField is null)
            throw new MissingFieldException(entryType.FullName, "InternalName");
        foreach (object? candidate in entries)
        {
            if (candidate is null || internalNameField.GetValue(candidate) is not string internalName)
                continue;
            CompanionMenuIdentitySnapshot? existingIdentity = identities.FirstOrDefault(identity => internalName == InternalName(identity.Identity));
            if (existingIdentity is not null)
                this.injectedEntries[candidate] = existingIdentity;
        }
        if (this.injectedEntries.Count == identities.Count)
            return;

        ConstructorInfo? constructor = entryType.GetConstructor(InstanceFields, binder: null,
            new[] { typeof(NPC), typeof(Friendship), typeof(CharacterData), typeof(string) }, modifiers: null);
        if (constructor is null)
            throw new MissingMethodException(entryType.FullName, ".ctor(NPC, Friendship, CharacterData, string)");

        int insertAt = numFarmers;
        foreach (CompanionMenuIdentitySnapshot identity in identities)
        {
            string internalName = InternalName(identity.Identity);
            if (this.injectedEntries.Values.Any(existing => existing.Identity == identity.Identity))
                continue;
            (NPC character, CharacterData data) = CompanionMenuIdentityFactory.CreateNativeSocialNpc(identity, this.GetTransparentNpcSprite());
            var friendship = new Friendship(identity.BondPoints)
            {
                TalkedToToday = identity.TalkedToday,
                GiftsToday = 0,
            };
            object entry = constructor.Invoke(new object[] { character, friendship, data, string.Empty });
            SetEntryField(entryType, entry, "InternalName", internalName);
            SetEntryField(entryType, entry, "DisplayName", identity.DisplayName);
            SetEntryField(entryType, entry, "IsMet", true);
            SetEntryField(entryType, entry, "IsDatable", false);
            SetEntryField(entryType, entry, "IsPlayer", false);
            SetEntryField(entryType, entry, "HeartLevel", identity.HeartLevel);
            entries.Insert(insertAt++, entry);
            this.injectedEntries.Add(entry, identity);
        }
        // NPC rows begin after the vanilla Farmer block. Keeping numFarmers unchanged is what
        // makes SocialPage use its ordinary NPC layout, hearts, buttons, and ProfileMenu path.
    }

    private void DrawSocialPortraits(SocialPage page, SpriteBatch batch)
    {
        if (SocialEntriesField?.GetValue(page) is not IList entries
            || CharacterSlotsField?.GetValue(page) is not IList slots
            || SlotPositionField?.GetValue(page) is not int slotPosition)
            throw new MissingFieldException(typeof(SocialPage).FullName, "SocialEntries/characterSlots/slotPosition");

        for (int slot = 0; slot < slots.Count; slot++)
        {
            int entryIndex = slotPosition + slot;
            if (entryIndex < 0 || entryIndex >= entries.Count || slots[slot] is not ClickableComponent component)
                continue;
            object entry = entries[entryIndex]!;
            if (this.TryResolveIdentity(entry, out CompanionMenuIdentitySnapshot? identity))
                this.portraits.Draw(batch, identity, new Vector2(component.bounds.X + 22, component.bounds.Y + 6));
        }
    }

    private void DrawProfilePortrait(ProfileMenu profile, SpriteBatch batch)
    {
        try
        {
            object? entry = ProfileCurrentField?.GetValue(profile);
            if (entry is not null
                && ProfileDrawPositionField?.GetValue(profile) is Vector2 position
                && this.TryResolveIdentity(entry, out CompanionMenuIdentitySnapshot? identity))
            {
                int facing = ProfileDirectionField?.GetValue(profile) as int? ?? 2;
                this.portraits.Draw(batch, identity, position, facing);
            }
        }
        catch (Exception ex)
        {
            if (!this.warnedLayout)
            {
                this.warnedLayout = true;
                this.monitor.Log($"HY-PROFILE-PORTRAIT-DISABLED: Native ProfileMenu portrait integration failed safely ({ex.GetType().Name}: {ex.Message}).", LogLevel.Warn);
            }
        }
    }

    private bool TryResolveIdentity(object entry, out CompanionMenuIdentitySnapshot identity)
    {
        if (this.injectedEntries.TryGetValue(entry, out identity!))
            return true;
        FieldInfo? internalNameField = entry.GetType().GetField("InternalName", InstanceFields);
        if (internalNameField?.GetValue(entry) is string internalName)
        {
            CompanionMenuIdentitySnapshot? match = this.socialMenu.GetIdentityView().FirstOrDefault(candidate => internalName == InternalName(candidate.Identity));
            if (match is not null)
            {
                identity = match;
                return true;
            }
        }
        identity = null!;
        return false;
    }

    private Texture2D GetTransparentNpcSprite()
    {
        if (this.transparentNpcSprite is not null && !this.transparentNpcSprite.IsDisposed)
            return this.transparentNpcSprite;
        this.transparentNpcSprite = new Texture2D(Game1.graphics.GraphicsDevice, 16, 32);
        this.transparentNpcSprite.SetData(new Color[16 * 32]);
        return this.transparentNpcSprite;
    }

    private static string InternalName(CompanionIdentity identity) => $"Himifox.YuiToIssho/{identity.OwnerId}/{identity.Slot}";

    private static void SetEntryField(Type entryType, object entry, string name, object value)
    {
        FieldInfo? field = entryType.GetField(name, InstanceFields);
        if (field is null)
            throw new MissingFieldException(entryType.FullName, name);
        field.SetValue(entry, value);
    }

    private static bool TryGetCurrentSocialPage(GameMenu menu, out SocialPage page)
    {
        page = null!;
        if (CurrentTabField?.GetValue(menu) is not int index || PagesField?.GetValue(menu) is not IList pages
            || index < 0 || index >= pages.Count || pages[index] is not SocialPage current)
            return false;
        page = current;
        return true;
    }

    private void DisableAdapter(Exception ex)
    {
        this.disabledPage = this.activePage;
        if (this.activePage is SocialPage page && SocialEntriesField?.GetValue(page) is IList entries)
        {
            foreach (object entry in this.injectedEntries.Keys.ToArray())
                entries.Remove(entry);
            NumFarmersField?.SetValue(page, this.originalFarmerCount);
        }
        this.injectedEntries.Clear();
        if (!this.warnedLayout)
        {
            this.warnedLayout = true;
            this.monitor.Log($"HY-SOCIAL-ENTRY-DISABLED: SocialPage integration failed safely ({ex.GetType().Name}: {ex.Message}). No replacement panel will be opened.", LogLevel.Warn);
        }
    }

    private void ResetPage()
    {
        this.activePage = null;
        this.disabledPage = null;
        this.originalFarmerCount = 0;
        this.injectedEntries.Clear();
        this.portraits.Clear();
    }

    private void ReleaseRuntime()
    {
        this.ResetPage();
        this.transparentNpcSprite?.Dispose();
        this.transparentNpcSprite = null;
    }
}
