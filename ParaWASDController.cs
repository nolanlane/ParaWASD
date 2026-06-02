using System;
using System.Collections.Generic;
using Setting;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace ParaWASD
{
    public class ParaWASDController : MonoBehaviour
    {
        public static ParaWASDController ActiveInstance { get; private set; }

        // State
        public bool IsActive { get; private set; }
        public bool IsCursorMode => _cursorMode;
        public bool IsInConversation => IsConversationActive;
        public bool BlocksDeactivation => IsConversationActive;
        private float _pitch;
        private float _yaw;
        private Transform _headBone;
        private Camera _gameCamera;
        private Vector3 _savedCameraPosition;
        private Quaternion _savedCameraRotation;
        private float _savedCameraFOV;
        private float _savedNearClip;


        // Mouse mode
        private bool _cursorMode;
        public bool IsLookMode => IsActive && !_cursorMode;

        // GUID of the Para currently being driven. The autonomy-injection patch reads this to block
        // autonomous interactions on exactly this one Para (and no one else) while we are active.
        public ulong FollowedCharacterGUID => _followedCharacterGUID;

        // True when the game should not inject autonomy onto the given character: it is the Para we
        // are actively driving in first person. Returns false for everyone else, so the rest of the
        // household (and the whole town) keeps its normal vanilla autonomy untouched.
        public static bool ShouldSuppressAutonomyFor(ulong characterGUID)
        {
            var c = ActiveInstance;
            return c != null && c.IsActive && characterGUID != 0 && characterGUID == c._followedCharacterGUID;
        }

        // Character reference
        private ulong _followedCharacterGUID;
        private CharacterVisual _followedVisual;
        private bool _usingFallbackHeadTransform;
        private bool _snapCameraNextFrame;

        // Fallback path traversal
        private NavMeshPath _navPath;
        private Vector3[] _pathCorners;
        private int _pathIndex;
        private bool _isTraversingPath;

        // Door tracking
        private float _doorCheckTimer;
        private const float DoorCheckInterval = 0.1f;
        private const float DoorOpenDistance = 2.2f;
        private const float DoorFreePassRadius = 1.2f;
        private const float DoorCloseDistance = 2.8f;
        private HashSet<ItemObjectDoor> _activeDoors = new HashSet<ItemObjectDoor>();

        // Mouse input
        private Vector2 _mouseDeltaThisFrame;
        private int _suppressMouseLookFrames;
        private bool _cursorModeLastFrame;

        // Interaction menu keyboard nav
        private int _menuSelectedIndex = -1;
        private int _menuActiveDepth;
        private Dictionary<int, int> _menuSelectedIndexByDepth = new Dictionary<int, int>();
        private bool _menuWasVisible;
        private UIInteractionsListItem _lastHoveredItem;
        private int _suppressInteractionMenuInputFrames;
        private bool _pauseMenuWasVisible;
        private bool _pauseForcedCursorMode;
        private bool _cursorModeBeforePause;

        // First-person conversation dialog
        private ParaConversationDialog _conversationDialog;
        private ConversationState _conversationState = ConversationState.None;
        private ulong _conversationTargetGUID;
        private ulong _conversationSocialGroupGUID;
        private ulong _conversationInteractionGUID;
        private bool _conversationHasObservedRunning;
        private int _togetherCardSelectedIndex = -1;
        private UITogetherCard _lastHoveredTogetherCard;
        private ParaTogetherCardsView _cardsView;
        private enum VanillaChromeState
        {
            // Everything visible (outcome reveal, or not in card mode).
            Shown,
            // All together chrome hidden — our custom card view is driving.
            Hidden,
            // Cards/storyteller/NPC panels hidden and non-interactive, but the dimmed
            // backdrop stays up so the player can see the portrait picker during target select.
            TargetPicker,
        }
        private readonly List<ParaTogetherCardsView.CardData> _cardDataBuffer = new List<ParaTogetherCardsView.CardData>();

        // Target-character selection (some cards target another Para before resolving).
        private int _targetSelectedIndex = -1;
        private readonly List<ulong> _targetCandidateBuffer = new List<ulong>();
        private GameObject _lastHighlightedCharacterItem;
        private static System.Reflection.FieldInfo _canBeSelectedField;

        // Stair traversal support
        private const float MinimumAcceptedMoveProgress = 0.02f;
        private const float StairPathLookAhead = 2.0f;
        private const float StairLinkStartRadius = 0.9f;
        private const float StairLinkDirectionDotMinimum = 0.55f;
        // A stair/step path must head roughly where the player is steering. ~0.25 (about 75 deg)
        // is loose enough for real stairs (which run forward) but rejects sideways/backtracking
        // detours that CalculatePath returns when the straight route is briefly blocked.
        private const float StairPathDirectionDotMinimum = 0.25f;

        private bool IsConversationActive => _conversationState != ConversationState.None;

        private void Start()
        {
            _navPath = new NavMeshPath();

            Activate();
        }

        /// <summary>
        /// Keep the cursor locked and read per-frame mouse delta. With a locked
        /// cursor, position is intentionally stationary; delta is the signal.
        /// </summary>
        private void Update()
        {
            if (!IsActive) return;

            if (IsConversationActive)
            {
                if (IsPauseMenuVisible())
                {
                    // The pause menu needs a free, visible cursor to be clickable.
                    ReleaseLookCursorLock();
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                    _mouseDeltaThisFrame = Vector2.zero;
                    _cursorModeLastFrame = true;
                    return;
                }

                // Stay in look mode with the cursor locked facing the Para. The whole
                // conversation is keyboard-driven, so free mouse look is suppressed and
                // the view holds wherever the camera was pointed.
                ForceLookCursorLock();
                _mouseDeltaThisFrame = Vector2.zero;
                _cursorModeLastFrame = false;
                return;
            }

            if (!_cursorMode)
            {
                // Returning to look mode from cursor mode: the OS pointer is wherever
                // the user left it, so the displacement-from-center would be read as a
                // huge mouse delta and snap the camera. Recenter and skip that frame's
                // delta so the view stays pointed where cursor mode left it.
                if (_cursorModeLastFrame)
                    _suppressMouseLookFrames = Mathf.Max(_suppressMouseLookFrames, 1);

                _mouseDeltaThisFrame = ReadMouseDelta();
                ForceLookCursorLock();
            }
            else
            {
                ReleaseLookCursorLock();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _mouseDeltaThisFrame = Vector2.zero;
            }

            _cursorModeLastFrame = _cursorMode;
        }

        public void Activate()
        {
            if (!TryAcquireTarget())
            {
                Debug.LogWarning("[ParaWASD] No active character to follow.");
                return;
            }

            SaveCameraState();
            IsActive = true;
            ActiveInstance = this;
            _cursorMode = false;

            _gameCamera.nearClipPlane = 0.05f;
            SetCharacterVisible(false);
            SetWallModeOverride(true);

            ForceLookCursorLock();
            _mouseDeltaThisFrame = Vector2.zero;
            _suppressMouseLookFrames = 2;

            // Keep the followed Para obedient by clearing any queued actions on entry, then
            // cancelling autonomously-injected interactions each frame (see SuppressFollowedAutonomy).
            // We deliberately do NOT touch the global EnableAutonomyForSelectedCharacters setting:
            // it persists to disk, so a crash/force-quit while active would leave the user's
            // autonomy permanently disabled with no way for us to restore it.
            CancelCharacterActions();

            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset != null)
                _yaw = characterAsset.Data.Rotation.eulerAngles.y;
            _pitch = 0f;
            _isTraversingPath = false;
            _doorCheckTimer = 0f;
            _activeDoors.Clear();
            _menuSelectedIndex = -1;
            _menuActiveDepth = 0;
            _menuSelectedIndexByDepth.Clear();
            _menuWasVisible = false;
            _lastHoveredItem = null;
            _suppressInteractionMenuInputFrames = 0;
            _pauseMenuWasVisible = false;
            _pauseForcedCursorMode = false;
            _cursorModeBeforePause = false;
            _snapCameraNextFrame = true;
        }

        public void Deactivate()
        {
            EndConversation(cancelInteraction: true);
            CleanupAllDoors();
            SetCharacterVisible(true);
            SetWallModeOverride(false);

            IsActive = false;
            ActiveInstance = null;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            ReleaseLookCursorLock();
            RestoreCameraState();
            _headBone = null;
            _followedVisual = null;
            _isTraversingPath = false;
        }

        // The player's wall-visibility button (Outline / Camera / Full) is tuned for the
        // overhead camera: Outline shows walls as lines and Camera cuts away whichever walls
        // sit between the angled camera and the room. From an eye-level camera inside the room
        // both read as broken - you see through the building, and Camera mode pops walls in and
        // out as you turn. While first person is active we force the game's own OverrideFull
        // (the same flag it uses to temporarily show full walls during wall-item placement) so
        // you stand inside solid rooms regardless of the player's chosen mode. Clearing it on
        // exit drops straight back to their setting; the refresh flags make the change apply
        // immediately instead of on the next build-mode edit.
        private void SetWallModeOverride(bool on)
        {
            PlayerManager.OverrideFull = on;
            BuildModeRefreshManager.IsWallVisibilityModeRefreshed = false;
            BuildModeRefreshManager.IsFloorLayerVisibilityRefreshed = false;
        }

        // Keep the visible floor layer pinned to the floor the followed Para is standing on.
        // The game does this itself in UpdateFreeCamera's follow branch (terrain -> layer 0,
        // otherwise the Para's ZoneWallObject layer/lot), so floors above stay hidden and the
        // current floor keeps its ceiling. Our FreeCameraPatch suppresses that method while
        // active, so without this the layer freezes at toggle-in and walking the Para upstairs
        // would leave the upper floor hidden. We mirror the game's exact calls and values.
        private void FollowCharacterFloorLayer(Player player)
        {
            if (player == null || PlayerManager.Instance == null)
                return;

            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null)
                return;

            if (characterAsset.Data.IsOnTerrain)
                PlayerManager.Instance.SetPlayerCameraLayer(player, 0, 0uL);
            else if ((bool)characterAsset.Data.OnZoneWallObject)
                PlayerManager.Instance.SetPlayerCameraLayer(player, characterAsset.Data.OnZoneWallObject.Layer, characterAsset.Data.OnZoneWallObject.LotPlacedOnGUID);
        }

        // Cancels any autonomously-injected interactions on the followed Para so the game's
        // autonomy can't hijack the character the player is driving. Player-initiated actions
        // (interaction menu, conversations) are not autonomous, so they survive untouched.
        // Because this never disables autonomy globally, it resumes the instant the player
        // exits ParaWASD with no setting to restore.
        private void SuppressFollowedAutonomy()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null || InteractionManager.Instance == null)
                return;

            var interactions = characterAsset.Data.CurrentInteractionsInQueue;
            if (interactions == null)
                return;

            for (int i = interactions.Count - 1; i >= 0; i--)
            {
                var interaction = interactions[i];
                if (!interaction.IsFromAnyAutonomy)
                    continue;
                if (interaction.State == AssetCharacterDataInteractionState.ToBeCanceled ||
                    interaction.State == AssetCharacterDataInteractionState.ToBeDeleted ||
                    interaction.State == AssetCharacterDataInteractionState.Cancelling)
                    continue;
                InteractionManager.Instance.CancelInteraction(interaction);
            }
        }

        private void LateUpdate()
        {
            if (!IsActive) return;

            // Exit when the player leaves Live Mode. They can open Build Mode, Photo,
            // Terrain, the bulldozer, etc. while ParaWASD is active; those states drive the
            // camera through HybridCamera/UpdateFreeCamera, which our patches suppress whenever
            // IsActive, so staying active would freeze a first-person camera over the build UI.
            // GameStates is a plain field written from many places (Back, LotManager, build
            // tools) with no change event to hook, so we poll it here. A null player means we
            // are between scenes (e.g. a load), where the saved camera refs are stale anyway.
            // Deactivate() restores the camera and ends any open conversation cleanly.
            var statePlayer = GetPlayer();
            if (statePlayer == null || statePlayer.State != GameStates.LiveMode)
            {
                Deactivate();
                return;
            }

            HandlePauseCursorMode();

            if (IsConversationActive)
            {
                HandleConversation();
                UpdateCameraPosition();
                return;
            }

            // The pause menu forces cursor mode (HandlePauseCursorMode) and needs a free pointer
            // to be usable. Ignore the Left Alt look/cursor toggle while paused so the player
            // can't flip into look mode (or back) behind the menu and desync the cursor state.
            if (Input.GetKeyDown(KeyCode.LeftAlt) && !IsPauseMenuVisible())
                _cursorMode = !_cursorMode;

            if (_headBone == null || _followedVisual == null)
            {
                if (!TryAcquireTarget())
                {
                    Deactivate();
                    return;
                }
            }

            // While the interaction menu is open we stay in look mode (crosshair, no cursor) and
            // drive the menu from the keyboard - W/S/E/Q etc. are menu keys, not look/move keys -
            // so suppress the normal look-mode bindings and movement. The player can still press
            // Left Alt to reveal the mouse pointer for click selection (handled above).
            bool menuOpen = IsInteractionMenuOpen();

            if (!_cursorMode && !menuOpen)
            {
                HandleMouseLook();
                HandleCharacterSwapKeys();
                if (Input.GetKeyDown(KeyCode.C))
                    TryCancelCurrentAction();
                if (Input.GetKeyDown(KeyCode.E))
                    TryOpenCenterInteractionMenu();
            }

            // Mirror vanilla portrait-click selection onto the follow, but ONLY in cursor mode.
            // In look mode a left click selects whatever the crosshair is aimed at, and silently
            // swapping the followed Para off a crosshair click would be surprising — look-mode
            // swapping is keys-only (1-8 / [ ]). Cursor-mode portrait clicks remain the legit way.
            if (_cursorMode)
                SyncFollowFromSelection();

            SuppressFollowedAutonomy();

            bool charBusy = IsCharacterPerformingAction();
            if (!_cursorMode && !menuOpen && !charBusy)
            {
                if (_isTraversingPath)
                    HandlePathTraversal();
                else
                    HandleMovement();
            }
            else
            {
                _isTraversingPath = false;
            }

            _doorCheckTimer -= Time.deltaTime;
            if (_doorCheckTimer <= 0f)
            {
                _doorCheckTimer = DoorCheckInterval;
                HandleDoorProximity();
            }

            HandleInteractionMenuKeyboard();
            ApplyFirstPersonFog();
            FollowCharacterFloorLayer(statePlayer);
            UpdateCameraPosition();
        }

        // The game tunes fog density to camera zoom in UpdateFreeCamera: near-zoom uses
        // Astronomy.NearZoomDistanceFogDensity, far-zoom the heavier FarZoomDistanceFogDensity,
        // lerped by zoom distance. Our FreeCameraPatch suppresses that method while active, so
        // fog freezes at whatever the top-down camera last set (often hazy from a zoomed-out
        // view). First person is effectively maximally zoomed in, so drive fog to the near-zoom
        // value every frame here, matching the game's own near-zoom branch. We restore nothing:
        // the moment ParaWASD deactivates, UpdateFreeCamera resumes and recomputes fog itself.
        // Storyboard fog overrides still win, exactly as the vanilla expression orders them.
        private void ApplyFirstPersonFog()
        {
            var storyboard = StoryboardManager.Instance;
            if (storyboard != null && storyboard.CurrentlyPlayingStoryboardOverridesFogIntensity)
            {
                RenderSettings.fogDensity = storyboard.CurrentlyPlayingStoryboardFogIntensity;
                return;
            }

            var astronomy = Settings.Get<Astronomy>();
            if (astronomy != null)
                RenderSettings.fogDensity = astronomy.NearZoomDistanceFogDensity;
        }

        /// <summary>
        /// Returns true if the character is performing a non-locomotion action
        /// (sitting, using objects, etc.) that the game is animating.
        /// </summary>
        private bool IsCharacterPerformingAction()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null) return false;

            var interactions = characterAsset.Data.CurrentInteractionsInQueue;
            if (interactions == null) return false;

            for (int i = 0; i < interactions.Count; i++)
            {
                var interaction = interactions[i];
                if (interaction.State == AssetCharacterDataInteractionState.Running &&
                    interaction.IsActionPerformedVisually &&
                    !interaction.IsLocomotionRunning)
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryAcquireTarget()
        {
            if (PlayerManager.Instance == null || CharacterManager.Instance == null)
                return false;

            var player = PlayerManager.Instance.HybridPlayer1.Player;
            var hybridPlayer = PlayerManager.Instance.HybridPlayer1;
            _gameCamera = hybridPlayer.HybridCamera.Camera;

            _followedCharacterGUID = player.GetSelectedCharacterGUID();
            if (_followedCharacterGUID == 0)
                _followedCharacterGUID = player.CameraCurrentCharacterFollowTarget;

            if (_followedCharacterGUID == 0 && HouseholdManager.Instance != null && HouseholdManager.Instance.HasCurrentHousehold)
            {
                var householdData = HouseholdManager.Instance.CurrentHousehold.Data;
                if (householdData.Characters != null && householdData.Characters.Count > 0)
                    _followedCharacterGUID = householdData.Characters[0];
            }

            if (_followedCharacterGUID == 0)
                return false;

            player.CameraCurrentCharacterFollowTarget = _followedCharacterGUID;

            _followedVisual = CharacterManager.Instance.GetLoadedCharacterVisual(_followedCharacterGUID);
            if (_followedVisual == null)
                return false;

            // Make the followed Para the selected one, exactly like clicking its portrait would.
            // This is the normal single-Para selection, so it does not change anyone's autonomy
            // (that is driven purely by our InjectInteraction block on the followed Para). Mirrors
            // the swap path; SelectCharacter no-ops if selection is locked (e.g. the intro).
            if (player.GetSelectedCharacterGUID() != _followedCharacterGUID)
                CharacterManager.Instance.SelectCharacter(_followedCharacterGUID, 0);

            return BindHeadBone();
        }

        // Resolves the camera anchor for the current _followedVisual. Prefers the "Head" bone
        // (true first-person eye line); falls back to the visual's root transform with a higher
        // vertical offset when the skeleton has no named Head bone. Shared by initial target
        // acquisition and mid-session Para swaps.
        private bool BindHeadBone()
        {
            if (_followedVisual == null)
                return false;

            _usingFallbackHeadTransform = false;
            if (_followedVisual.BoneTransformByName != null &&
                _followedVisual.BoneTransformByName.TryGetValue("Head", out var headBoneData))
            {
                _headBone = headBoneData.Transform;
                _snapCameraNextFrame = true;
                return _headBone != null;
            }

            _headBone = _followedVisual.transform;
            _usingFallbackHeadTransform = true;
            _snapCameraNextFrame = true;
            return true;
        }

        // Ordered roster of the current household's Paras (matches the on-screen portrait order),
        // or null when there is no current household. Used for keyboard Para swapping.
        private List<ulong> GetHouseholdRoster()
        {
            if (HouseholdManager.Instance == null || !HouseholdManager.Instance.HasCurrentHousehold)
                return null;
            var data = HouseholdManager.Instance.CurrentHousehold.Data;
            return data != null ? data.Characters : null;
        }

        // [ and ] cycle to the previous/next household Para with wraparound. We deliberately do NOT
        // use the number keys for this: the base game maps them to time-speed controls in Live Mode,
        // so binding them here would fight the vanilla shortcut. Only runs in look mode (see caller).
        private void HandleCharacterSwapKeys()
        {
            var roster = GetHouseholdRoster();
            if (roster == null || roster.Count <= 1)
                return;

            int current = roster.IndexOf(_followedCharacterGUID);
            if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                int next = current < 0 ? 0 : (current + 1) % roster.Count;
                SwitchFollowedCharacter(roster[next]);
            }
            else if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                int prev = current < 0 ? 0 : (current - 1 + roster.Count) % roster.Count;
                SwitchFollowedCharacter(roster[prev]);
            }
        }

        // Mirror selection changes the player makes through the vanilla UI (e.g. clicking a
        // household portrait in cursor mode) onto the followed Para, so the two never drift apart.
        // Only household members are honored; a stray world click that selects a non-household
        // NPC is ignored so it can't hijack the first-person follow.
        private void SyncFollowFromSelection()
        {
            var player = GetPlayer();
            if (player == null)
                return;
            ulong selected = player.GetSelectedCharacterGUID();
            if (selected == 0 || selected == _followedCharacterGUID)
                return;
            var roster = GetHouseholdRoster();
            if (roster == null || !roster.Contains(selected))
                return;
            SwitchFollowedCharacter(selected);
        }

        // Swaps the first-person camera to a different household Para. Re-shows the previously
        // followed Para, binds the new head bone, hides the new Para's body, and syncs the game's
        // own selection + follow target so the vanilla portraits highlight correctly. No-ops when
        // the target is unchanged or not currently loaded (e.g. off-lot), leaving the current
        // follow intact rather than dropping out of look mode.
        private void SwitchFollowedCharacter(ulong newGUID)
        {
            if (newGUID == 0 || newGUID == _followedCharacterGUID || CharacterManager.Instance == null)
                return;

            // If the game currently forbids selection changes (e.g. the tutorial intro), don't
            // swap: vanilla SelectCharacter below would no-op, leaving the camera pointed at a Para
            // the game disagrees with, and SyncFollowFromSelection would then yank us back next
            // frame. Bailing keeps the follow and the game's selection in agreement.
            var player = GetPlayer();
            if (player != null && !player.CanChangeCharacterSelection())
                return;

            var newVisual = CharacterManager.Instance.GetLoadedCharacterVisual(newGUID);
            if (newVisual == null)
                return;

            // Re-show the Para we are leaving before we repoint at the new one.
            SetCharacterVisible(true);

            _followedCharacterGUID = newGUID;
            _followedVisual = newVisual;
            if (!BindHeadBone())
                return;

            CharacterManager.Instance.SelectCharacter(newGUID, 0);
            if (player != null)
                player.CameraCurrentCharacterFollowTarget = newGUID;

            SetCharacterVisible(false);

            // Same clean-slate handoff as Activate(): clear the new Para's queued actions and
            // pathfinding so it stops whatever autonomous task it was running and answers to the
            // player immediately, instead of walking off on the first frame before
            // SuppressFollowedAutonomy catches it.
            CancelCharacterActions();

            var asset = GetFollowedCharacterAsset();
            if (asset != null)
                _yaw = asset.Data.Rotation.eulerAngles.y;

            // Drop any in-progress traversal/menu state tied to the previous Para and snap the
            // camera to the new head so the swap reads as instant rather than a glide across the lot.
            _isTraversingPath = false;
            CleanupAllDoors();
            _doorCheckTimer = 0f;
            _menuSelectedIndex = -1;
            _menuActiveDepth = 0;
            _menuSelectedIndexByDepth.Clear();
            _snapCameraNextFrame = true;
            _suppressMouseLookFrames = 2;
        }

        private void HandleMouseLook()
        {
            Vector2 delta = _mouseDeltaThisFrame;
            if (delta.x == 0f && delta.y == 0f) return;

            float mouseSensitivity = Plugin.MouseSensitivity.Value;
            float pitchDelta = (Plugin.InvertMouseY.Value ? delta.y : -delta.y) * mouseSensitivity * 0.1f;
            _yaw += delta.x * mouseSensitivity * 0.1f;
            _pitch += pitchDelta;

            float pitchMin = Mathf.Min(Plugin.PitchMinimum.Value, Plugin.PitchMaximum.Value);
            float pitchMax = Mathf.Max(Plugin.PitchMinimum.Value, Plugin.PitchMaximum.Value);
            if (Mathf.Approximately(pitchMin, pitchMax))
                pitchMax = pitchMin + 1f;
            _pitch = Mathf.Clamp(_pitch, pitchMin, pitchMax);
        }

        private Vector2 ReadMouseDelta()
        {
            var center = (Vector2)GetScreenCenterPosition(0);
            var mouse = Mouse.current;
            Vector2 positionDelta = Vector2.zero;

            if (mouse != null)
            {
                Vector2 position = mouse.position.ReadValue();
                positionDelta = position - center;
            }

            Vector2 legacyDelta = new Vector2(
                Input.GetAxisRaw("Mouse X") * 10f,
                Input.GetAxisRaw("Mouse Y") * 10f);

            // Prefer explicit recenter movement. On some Unity/Input System
            // combinations, locked mouse deltas collapse when the hidden OS
            // pointer reaches the window edge.
            Vector2 delta = positionDelta.sqrMagnitude > 0.25f
                ? positionDelta
                : legacyDelta != Vector2.zero
                ? legacyDelta
                : Mouse.current?.delta.ReadValue() ?? Vector2.zero;

            if (_suppressMouseLookFrames > 0)
            {
                _suppressMouseLookFrames--;
                WarpMouseToCenter(center);
                return Vector2.zero;
            }

            WarpMouseToCenter(center);
            return delta;
        }

        public void ForceLookCursorLock()
        {
            var center = GetScreenCenterPosition(0);

            if (CursorManager.Instance != null)
            {
                CursorManager.Instance.MouseLockedInPlace = true;
                CursorManager.Instance.MouseLockedPosition = center;
                CursorManager.Instance.CursorIsVisible = false;
                CursorManager.Instance.CursorLockMode = CursorLockMode.Locked;
            }

            if (IsConversationActive)
            {
                // No free mouse look during a conversation, so hard-lock the OS pointer
                // to center. This stops it roaming over (and clicking) UI elements via
                // the EventSystem, which reads the real pointer position.
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                return;
            }

            // Keep the OS cursor movable so we can measure displacement from
            // center, then warp it back before it reaches the screen edge.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = false;
        }

        private static void WarpMouseToCenter(Vector2 center)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            mouse.WarpCursorPosition(center);
            InputState.Change(mouse.position, center);
        }

        public void ReleaseLookCursorLock()
        {
            if (CursorManager.Instance == null) return;

            CursorManager.Instance.MouseLockedInPlace = false;
            CursorManager.Instance.CursorIsVisible = true;
            CursorManager.Instance.CursorLockMode = CursorLockMode.None;
        }

        public static Vector3 GetScreenCenterPosition(int playerIndex)
        {
            if (PlayerManager.Instance != null)
            {
                var hybridPlayer = PlayerManager.Instance.GetHybridPlayer(playerIndex);
                if (hybridPlayer != null && hybridPlayer.ScreenCenterPosition != Vector3.zero)
                    return hybridPlayer.ScreenCenterPosition;
            }

            return new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        }

        private void HandleMovement()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null) return;

            float h = 0f, v = 0f;
            if (Input.GetKey(KeyCode.W)) v += 1f;
            if (Input.GetKey(KeyCode.S)) v -= 1f;
            if (Input.GetKey(KeyCode.A)) h -= 1f;
            if (Input.GetKey(KeyCode.D)) h += 1f;

            if (h == 0f && v == 0f) return;

            var forward = new Vector3(Mathf.Sin(_yaw * Mathf.Deg2Rad), 0, Mathf.Cos(_yaw * Mathf.Deg2Rad));
            var right = new Vector3(forward.z, 0, -forward.x);
            var moveDir = (forward * v + right * h).normalized;

            float speed = GetMoveSpeed();

            var currentPos = characterAsset.Data.Position;
            var desiredPos = currentPos + moveDir * speed * Time.deltaTime;
            int areaMask = GetFullAreaMask();

            if (IsNearAnyActiveDoor(currentPos))
            {
                if (NavMesh.SamplePosition(desiredPos, out var dh, 2.0f, areaMask))
                    desiredPos.y = dh.position.y;
                else
                    desiredPos.y = TerrainManager.Instance.GetHeightAtWorldPosition(desiredPos);

                characterAsset.Data.Position = desiredPos;
                characterAsset.Data.Rotation = Quaternion.Euler(0, _yaw, 0);
                return;
            }

            if (TryNavMeshMove(characterAsset, desiredPos, 0.2f, areaMask, requireProgress: true)) return;
            if (TryNavMeshMove(characterAsset, desiredPos, 0.5f, areaMask, requireProgress: true)) return;

            // Fallback for reachable elevation changes (the stair candidate path).
            if (TryStartPathTo(characterAsset, currentPos + moveDir * StairPathLookAhead, moveDir)) return;
            if (TryStartNearestStairLinkTraversal(characterAsset, moveDir)) return;

            var slideX = new Vector3(desiredPos.x, currentPos.y, currentPos.z);
            if (TryNavMeshMove(characterAsset, slideX, 0.2f, areaMask, requireProgress: true)) return;
            var slideZ = new Vector3(currentPos.x, currentPos.y, desiredPos.z);
            TryNavMeshMove(characterAsset, slideZ, 0.2f, areaMask, requireProgress: true);
        }

        private bool TryNavMeshMove(AssetCharacter characterAsset, Vector3 pos, float radius, int areaMask,
                                    bool requireProgress = false)
        {
            if (!NavMesh.SamplePosition(pos, out var hit, radius, areaMask))
                return false;

            float yDiff = Mathf.Abs(hit.position.y - characterAsset.Data.Position.y);
            if (yDiff > 0.3f)
                return false;

            if (requireProgress)
            {
                var requested = pos - characterAsset.Data.Position;
                requested.y = 0f;
                var actual = hit.position - characterAsset.Data.Position;
                actual.y = 0f;

                float requestedDist = requested.magnitude;
                if (requestedDist > MinimumAcceptedMoveProgress)
                {
                    float progress = Vector3.Dot(actual, requested / requestedDist);
                    float requiredProgress = Mathf.Min(MinimumAcceptedMoveProgress, requestedDist * 0.35f);
                    if (progress < requiredProgress)
                        return false;
                }
            }

            characterAsset.Data.Position = hit.position;
            characterAsset.Data.Rotation = Quaternion.Euler(0, _yaw, 0);
            return true;
        }

        private int GetStairsAreaMaskBit()
        {
            if (UnityLayersManager.Instance != null)
                return 1 << UnityLayersManager.Instance.NavmeshArea.Stairs;
            return 0;
        }

        private bool IsNearAnyActiveDoor(Vector3 pos)
        {
            foreach (var door in _activeDoors)
            {
                if (door == null || door.transform == null) continue;
                if (Vector3.Distance(pos, door.transform.position) < DoorFreePassRadius)
                    return true;
            }
            return false;
        }

        private bool TryStartPathTo(AssetCharacter characterAsset, Vector3 targetPos, Vector3 moveDir)
        {
            int areaMask = GetFullAreaMask() | GetStairsAreaMaskBit();
            if (!NavMesh.SamplePosition(targetPos, out var targetHit, 2.0f, areaMask))
                return false;

            if (!NavMesh.SamplePosition(characterAsset.Data.Position, out var startHit, 1.0f, areaMask))
                return false;

            NavMesh.CalculatePath(startHit.position, targetHit.position, areaMask, _navPath);
            if (_navPath.status == NavMeshPathStatus.PathInvalid)
                return false;

            _pathCorners = _navPath.corners;
            if (_pathCorners.Length < 2)
                return false;

            float maxYDiff = 0f;
            float totalLength = 0f;
            for (int i = 1; i < _pathCorners.Length; i++)
            {
                float yDiff = Mathf.Abs(_pathCorners[i].y - _pathCorners[0].y);
                if (yDiff > maxYDiff) maxYDiff = yDiff;
                totalLength += Vector3.Distance(_pathCorners[i - 1], _pathCorners[i]);
            }
            if (maxYDiff < 0.3f)
                return false;

            // Don't seize control for a path that heads somewhere other than where the player is
            // steering. When the straight route is briefly blocked, CalculatePath can return a
            // detour that veers sideways or doubles back; auto-walking it is the "caught on an
            // invisible path" feeling. A real stair/step in front of the player runs in the move
            // direction, so checking both the overall and first-segment direction keeps stairs
            // working while dropping off-axis detours.
            var wantDir = new Vector2(moveDir.x, moveDir.z);
            if (wantDir.sqrMagnitude > 0.0001f)
            {
                wantDir.Normalize();

                var endFlat = _pathCorners[_pathCorners.Length - 1] - _pathCorners[0];
                var endDir = new Vector2(endFlat.x, endFlat.z);
                if (endDir.sqrMagnitude > 0.0001f &&
                    Vector2.Dot(wantDir, endDir.normalized) < StairPathDirectionDotMinimum)
                    return false;

                for (int i = 1; i < _pathCorners.Length; i++)
                {
                    var segFlat = _pathCorners[i] - _pathCorners[0];
                    var segDir = new Vector2(segFlat.x, segFlat.z);
                    if (segDir.sqrMagnitude < 0.0025f) // skip corners within ~5cm of the start
                        continue;
                    if (Vector2.Dot(wantDir, segDir.normalized) < StairPathDirectionDotMinimum)
                        return false;
                    break;
                }
            }

            // A genuine step/stair sits within the look-ahead; a path much longer than that is a
            // detour that merely happens to cross an elevation change.
            if (totalLength > StairPathLookAhead * 2f)
                return false;

            _pathIndex = 1;
            _isTraversingPath = true;
            return true;
        }

        private bool TryStartNearestStairLinkTraversal(AssetCharacter characterAsset, Vector3 moveDir)
        {
            if (UnityLayersManager.Instance == null)
                return false;

            int stairsArea = UnityLayersManager.Instance.NavmeshArea.Stairs;
            var pos = characterAsset.Data.Position;
            var links = UnityEngine.Object.FindObjectsOfType<NavMeshLink>();

            NavMeshLink bestLink = null;
            Vector3 bestStart = Vector3.zero;
            Vector3 bestEnd = Vector3.zero;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                if (link == null || link.transform == null || link.area != stairsArea)
                    continue;

                var worldStart = link.transform.rotation * link.startPoint + link.transform.position;
                var worldEnd = link.transform.rotation * link.endPoint + link.transform.position;

                if (PathfindingManager.Instance != null)
                {
                    var lr = PathfindingManager.Instance.NavMeshLinkList.FindLink(worldStart, worldEnd);
                    if (lr.PathLink != null && lr.PathLink.NavMeshLinkType != NavMeshLinkType.Stairs)
                        continue;
                }

                var toStart = worldStart - pos;
                toStart.y = 0f;
                float startDistance = toStart.magnitude;
                if (startDistance > StairLinkStartRadius)
                    continue;

                var linkDir = worldEnd - worldStart;
                linkDir.y = 0f;
                if (linkDir.sqrMagnitude < 0.01f)
                    continue;

                float directionDot = Vector3.Dot(moveDir, linkDir.normalized);
                if (directionDot < StairLinkDirectionDotMinimum)
                    continue;

                float score = directionDot * 2f - startDistance;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestLink = link;
                bestStart = worldStart;
                bestEnd = worldEnd;
            }

            if (bestLink == null)
                return false;

            _pathCorners = new[] { pos, bestStart, bestEnd };
            _pathIndex = 1;
            _isTraversingPath = true;
            return true;
        }

        private void HandlePathTraversal()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null || _pathCorners == null)
            {
                _isTraversingPath = false;
                return;
            }

            float speed = GetMoveSpeed();
            if (Input.GetKey(KeyCode.S))
            {
                _isTraversingPath = false;
                return;
            }

            var currentTarget = _pathCorners[_pathIndex];
            var toTarget = currentTarget - characterAsset.Data.Position;
            float dist = toTarget.magnitude;

            if (dist < 0.15f)
            {
                _pathIndex++;
                if (_pathIndex >= _pathCorners.Length)
                {
                    characterAsset.Data.Position = _pathCorners[_pathCorners.Length - 1];
                    _isTraversingPath = false;
                    return;
                }
            }
            else
            {
                var step = toTarget.normalized * speed * Time.deltaTime;
                if (step.magnitude > dist) step = toTarget;
                characterAsset.Data.Position += step;
                if (toTarget.x != 0f || toTarget.z != 0f)
                    characterAsset.Data.Rotation = Quaternion.Euler(0,
                        Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg, 0);
            }
        }

        private void HandleDoorProximity()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null || ItemManager.Instance == null) return;

            var doors = ItemManager.Instance.Doors;
            var charPos = characterAsset.Data.Position;

            for (int i = 0; i < doors.Count; i++)
            {
                var door = doors[i];
                if (door == null || door.transform == null) continue;
                float dist = Vector3.Distance(charPos, door.transform.position);

                if (dist < DoorOpenDistance && !_activeDoors.Contains(door))
                {
                    PathfindingManager.Instance.AddCharacterAtDoor(characterAsset, door);
                    _activeDoors.Add(door);
                }
                else if (dist > DoorCloseDistance && _activeDoors.Contains(door))
                {
                    PathfindingManager.Instance.RemoveCharacterAtDoor(characterAsset, door);
                    _activeDoors.Remove(door);
                }
            }
        }

        private void CleanupAllDoors()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null) return;
            foreach (var door in _activeDoors)
            {
                if (door != null)
                    PathfindingManager.Instance.RemoveCharacterAtDoor(characterAsset, door);
            }
            _activeDoors.Clear();
        }

        /// <summary>
        /// Arrow/WASD keys navigate interaction menu lists and submenus.
        /// Properly clears hover on the old item before highlighting the new one.
        /// </summary>
        private bool IsInteractionMenuOpen()
        {
            var uiInteractions = UI.GetOrNull<UIInteractions>(0);
            return uiInteractions != null && uiInteractions.IsVisible;
        }

        private void HandleInteractionMenuKeyboard()
        {
            var uiInteractions = UI.GetOrNull<UIInteractions>(0);
            bool menuVisible = uiInteractions != null && uiInteractions.IsVisible;

            // Menu just opened. Stay in look mode (crosshair, no cursor) and pre-select the first
            // item so keyboard navigation works immediately. The player reveals the mouse pointer
            // on demand with Left Alt; we do not force cursor mode here.
            if (menuVisible && !_menuWasVisible)
            {
                _menuSelectedIndex = 0;
                _menuActiveDepth = 0;
                _menuSelectedIndexByDepth.Clear();
                _menuSelectedIndexByDepth[0] = 0;
                HighlightInteractionItem(uiInteractions, 0, 0);
            }
            // Menu just closed: drop any Alt-revealed cursor and return to look mode.
            else if (!menuVisible && _menuWasVisible)
            {
                _cursorMode = false;
                _menuSelectedIndex = -1;
                _menuActiveDepth = 0;
                _menuSelectedIndexByDepth.Clear();
                ClearHoveredInteractionItem();
            }
            _menuWasVisible = menuVisible;

            if (!menuVisible) return;

            // In keyboard mode we don't run the mouse-look recenter, so keep the hidden pointer
            // parked at screen center (where the menu opens). Then if the player taps Left Alt to
            // reveal the cursor, it appears over the menu instead of wherever it last drifted. Once
            // the cursor is showing we leave it alone so they can move freely to click an item.
            if (!_cursorMode)
                WarpMouseToCenter((Vector2)GetScreenCenterPosition(0));

            // Q closes the interaction menu, mirroring its use elsewhere (back out / cancel).
            if (Input.GetKeyDown(KeyCode.Q))
            {
                uiInteractions.Back();
                return;
            }

            if (_suppressInteractionMenuInputFrames > 0)
            {
                _suppressInteractionMenuInputFrames--;
                return;
            }

            SyncMenuDepth(uiInteractions);
            var activeList = GetInteractionList(uiInteractions, _menuActiveDepth);
            if (activeList == null) return;

            int itemCount = activeList.UIListInteractionItems.CurrentItems.Count;
            if (itemCount == 0) return;

            if (!_menuSelectedIndexByDepth.TryGetValue(_menuActiveDepth, out _menuSelectedIndex))
                _menuSelectedIndex = 0;
            _menuSelectedIndex = Mathf.Clamp(_menuSelectedIndex, 0, itemCount - 1);
            _menuSelectedIndexByDepth[_menuActiveDepth] = _menuSelectedIndex;

            bool changed = false;
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S))
            {
                _menuSelectedIndex = (_menuSelectedIndex + 1) % itemCount;
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
            {
                _menuSelectedIndex = (_menuSelectedIndex - 1 + itemCount) % itemCount;
                changed = true;
            }

            if (changed)
            {
                _menuSelectedIndexByDepth[_menuActiveDepth] = _menuSelectedIndex;
                HighlightInteractionItem(uiInteractions, _menuActiveDepth, _menuSelectedIndex);
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A))
            {
                CloseActiveInteractionSubmenu(uiInteractions);
                return;
            }

            bool openSubmenu = Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D);
            bool submit = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E);

            if (openSubmenu || submit)
            {
                var item = GetInteractionItem(uiInteractions, _menuActiveDepth, _menuSelectedIndex);
                if (item == null) return;

                bool isGroup = item.InteractionGroupItem.Type == InteractionItemType.Group;
                if (openSubmenu && !isGroup)
                    return;

                ClickInteractionItem(item);

                if (isGroup)
                {
                    SyncMenuDepth(uiInteractions, preferDeeper: true);
                    _menuSelectedIndexByDepth[_menuActiveDepth] = 0;
                    HighlightInteractionItem(uiInteractions, _menuActiveDepth, 0);
                }
            }
        }

        private void SyncMenuDepth(UIInteractions ui, bool preferDeeper = false)
        {
            int maxDepth = GetMaxInteractionListDepth(ui);
            if (preferDeeper)
                _menuActiveDepth = maxDepth;
            else
                _menuActiveDepth = Mathf.Clamp(_menuActiveDepth, 0, maxDepth);
        }

        private int GetMaxInteractionListDepth(UIInteractions ui)
        {
            if (ui == null || ui.UIInteractionLists == null)
                return 0;

            int maxDepth = 0;
            for (int i = 0; i < ui.UIInteractionLists.CurrentItems.Count; i++)
            {
                if (ui.UIInteractionLists.CurrentItems[i] is UIInteractionsList list)
                    maxDepth = Mathf.Max(maxDepth, list.ListDepth);
            }
            return maxDepth;
        }

        private UIInteractionsList GetInteractionList(UIInteractions ui, int depth)
        {
            if (ui == null || ui.UIInteractionLists == null)
                return null;

            for (int i = 0; i < ui.UIInteractionLists.CurrentItems.Count; i++)
            {
                if (ui.UIInteractionLists.CurrentItems[i] is UIInteractionsList list && list.ListDepth == depth)
                    return list;
            }
            return null;
        }

        private void CloseActiveInteractionSubmenu(UIInteractions ui)
        {
            if (_menuActiveDepth <= 0 || ui?.UIInteractionLists == null)
                return;

            ClearHoveredInteractionItem();
            ui.UIInteractionLists.DeactivateAndPoolAllItemsAtIndexAndAfter(_menuActiveDepth);
            _menuSelectedIndexByDepth.Remove(_menuActiveDepth);
            _menuActiveDepth--;
            if (!_menuSelectedIndexByDepth.TryGetValue(_menuActiveDepth, out _menuSelectedIndex))
                _menuSelectedIndex = 0;
            HighlightInteractionItem(ui, _menuActiveDepth, _menuSelectedIndex);
        }

        private void HighlightInteractionItem(UIInteractions ui, int depth, int index)
        {
            if (EventSystem.current == null) return;

            ClearHoveredInteractionItem();

            var item = GetInteractionItem(ui, depth, index);
            if (item != null)
            {
                // Trigger hover on new item
                var enterData = new PointerEventData(EventSystem.current)
                {
                    position = item.RectTransform.position
                };
                ExecuteEvents.Execute(item.gameObject, enterData, ExecuteEvents.pointerEnterHandler);
                EventSystem.current.SetSelectedGameObject(item.gameObject);
                _lastHoveredItem = item;
            }
        }

        private void ClearHoveredInteractionItem()
        {
            if (EventSystem.current == null)
            {
                _lastHoveredItem = null;
                return;
            }

            if (_lastHoveredItem != null && (UnityEngine.Object)_lastHoveredItem != null && _lastHoveredItem.gameObject != null)
            {
                var exitData = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(_lastHoveredItem.gameObject, exitData, ExecuteEvents.pointerExitHandler);
            }
            _lastHoveredItem = null;
        }

        private UIInteractionsListItem GetInteractionItem(UIInteractions ui, int depth, int index)
        {
            if (index < 0) return null;
            var interactionList = GetInteractionList(ui, depth);
            if (interactionList == null) return null;
            var list = interactionList.UIListInteractionItems;
            if (index >= list.CurrentItems.Count) return null;
            return list.GetItemAtIndex<UIInteractionsListItem>(index);
        }

        private void ClickInteractionItem(UIInteractionsListItem item)
        {
            if (item == null || EventSystem.current == null) return;

            var eventData = new PointerEventData(EventSystem.current);
            ExecuteEvents.Execute(item.gameObject, eventData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(item.gameObject, eventData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(item.gameObject, eventData, ExecuteEvents.pointerClickHandler);
        }

        private void TryOpenCenterInteractionMenu()
        {
            if (!Plugin.CenterInteractEnabled.Value || _gameCamera == null)
                return;

            var player = GetPlayer();
            if (player == null || player.State != GameStates.LiveMode || player.GetSelectedCharacterGUID() == 0)
                return;

            var uiInteractions = UI.GetOrNull<UIInteractions>(player.PlayerIndex);
            if (uiInteractions != null && uiInteractions.IsVisible)
                return;

            var hit = RaycastCenterForInteraction();
            if (hit.HasHit)
            {
                if (hit.ColliderType == ColliderType.Character && TryStartConversationDialog(player, hit))
                    return;

                if (OpenInteractionMenuForHit(player, hit))
                {
                    _isTraversingPath = false;
                    _suppressInteractionMenuInputFrames = 1;
                    return;
                }
            }

            // Our crosshair raycast missed or the hit had no interactions. Fall back to the game's
            // own center-hover raycast (UpdateHover already aims at screen center in look mode), so
            // E still opens a menu when the precise physics ray and the game's hover ray disagree.
            TryOpenPlayerRaycastInteractionMenu(player);
        }

        private CenterInteractionHit RaycastCenterForInteraction()
        {
            CenterInteractionHit result = default;
            if (_gameCamera == null || UnityLayersManager.Instance == null)
                return result;

            Physics.SyncTransforms();
            var ray = _gameCamera.ScreenPointToRay(GetScreenCenterPosition(0));
            int characterLayer = UnityLayersManager.Instance.GameObjects.CharacterVisual;
            int layerMask = UnityLayersManager.Instance.RaycastLayerMask | (1 << characterLayer);
            RaycastHit[] hits = Physics.RaycastAll(ray, Plugin.CenterInteractDistance.Value, layerMask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.collider == null) continue;

                var target = hit.collider.GetComponent<RaycastTarget>();
                if (target == null) continue;

                if (target.ColliderType == ColliderType.Terrain && RaycastSystem.CheckIfTerrainHole(hit))
                    continue;

                if (target.ColliderType == ColliderType.Character)
                {
                    if (!ulong.TryParse(target.transform.name, out ulong characterGUID))
                        continue;
                    if (characterGUID == _followedCharacterGUID)
                        continue;

                    result.HasHit = true;
                    result.ColliderType = target.ColliderType;
                    result.WorldPosition = hit.point;
                    result.CharacterGUID = characterGUID;
                    return result;
                }

                result.HasHit = true;
                result.ColliderType = target.ColliderType;
                result.WorldPosition = hit.point;
                result.RaycastObject = target.RaycastObject != null ? target.RaycastObject : hit.collider.gameObject;
                return result;
            }

            return result;
        }

        private bool TryOpenPlayerRaycastInteractionMenu(Player player)
        {
            if (player == null || !player.HasRaycastObject)
                return false;

            var hit = new CenterInteractionHit
            {
                HasHit = true,
                ColliderType = player.CurrentObjectRaycastMeshColliderType,
                RaycastObject = player.RaycastGameObject,
                WorldPosition = player.CurrentObjectRaycastWorldPosition
            };

            if (!OpenInteractionMenuForHit(player, hit))
                return false;

            _isTraversingPath = false;
            _suppressInteractionMenuInputFrames = 1;
            return true;
        }

        private bool OpenInteractionMenuForHit(Player player, CenterInteractionHit hit)
        {
            // createIfNotExists: the UIInteractions window is instantiated lazily the first time
            // the game shows it. Until the player left-clicks something once, UI.GetOrNull returns
            // null and E silently did nothing - which is why E "needed a click first". Asking the
            // UI system to create it on demand makes the crosshair menu work from the first frame.
            var uiInteractions = UI.GetOrNull<UIInteractions>(player.PlayerIndex, true);
            if (uiInteractions == null)
                return false;

            var interactions = Settings.Get<Interactions>();
            if (interactions == null)
                return false;

            if (hit.ColliderType == ColliderType.Object)
            {
                var item = ResolveItemRoot(hit.RaycastObject);
                if (item == null || !InteractionManager.Instance.ItemHasInteractions(item, player.PlayerIndex))
                    return false;

                var group = InteractionManager.Instance.GetItemInteractionGroup(item);
                uiInteractions.Show(group, hit.WorldPosition, item.InstanceID, 0UL, item, item.LotPlacedOnGUID);
                return true;
            }

            if (hit.ColliderType == ColliderType.Floor || hit.ColliderType == ColliderType.Terrain)
            {
                var group = interactions.GetInteractionGroupByGUID(interactions.FloorInteractions);
                if (group == null)
                    return false;

                ulong lotGUID = LotManager.Instance != null ? LotManager.Instance.GetLotFromPosition(hit.WorldPosition) : 0UL;
                uiInteractions.Show(group, hit.WorldPosition, -1, 0UL, null, lotGUID);
                return true;
            }

            if (hit.ColliderType == ColliderType.Character)
            {
                var targetCharacter = CharacterManager.Instance.GetCharacterByGUID(hit.CharacterGUID);
                if (targetCharacter == null || targetCharacter.Data.IsDead)
                    return false;

                ulong groupGUID = InteractionManager.Instance.GetInteractionGroupGUIDFor(interactions, player.SelectedCharactersGUID, hit.CharacterGUID);
                var group = interactions.GetInteractionGroupByGUID(groupGUID);
                if (group == null)
                    return false;

                ulong lotGUID = LotManager.Instance != null ? LotManager.Instance.GetLotFromPosition(hit.WorldPosition) : 0UL;
                bool canShow = false;
                foreach (ulong selectedGUID in player.SelectedCharactersGUID)
                {
                    if (InteractionManager.Instance.CanShowInteractionGroupInInteractionList(group, null, player.PlayerIndex, selectedGUID, hit.CharacterGUID, lotGUID))
                    {
                        canShow = true;
                        break;
                    }
                }

                if (!canShow)
                    return false;

                uiInteractions.Show(group, hit.WorldPosition, -1, hit.CharacterGUID, null, lotGUID);
                return true;
            }

            return false;
        }

        private ItemObjectRoot ResolveItemRoot(GameObject raycastObject)
        {
            if (raycastObject == null)
                return null;

            var item = raycastObject.GetComponent<ItemObjectRoot>();
            if (item != null)
                return item;

            item = raycastObject.GetComponentInParent<ItemObjectRoot>();
            if (item != null)
                return item;

            return raycastObject.GetComponentInChildren<ItemObjectRoot>();
        }

        private bool TryStartConversationDialog(Player player, CenterInteractionHit hit)
        {
            if (player == null || hit.ColliderType != ColliderType.Character || hit.CharacterGUID == 0UL)
                return false;

            var selectedGUID = player.GetSelectedCharacterGUID();
            if (selectedGUID == 0UL || selectedGUID == hit.CharacterGUID)
                return false;

            var speaker = AssetManager.Instance.GetCharacter(selectedGUID);
            var target = AssetManager.Instance.GetCharacter(hit.CharacterGUID);
            if (speaker == null || target == null || target.Data.IsDead)
                return false;

            if (!TryFindConversationInteraction(player, hit, selectedGUID, out ulong interactionGUID, out ulong lotGUID))
                return false;

            var targetData = new InteractionTarget
            {
                WorldPosition = hit.WorldPosition,
                CharacterGUID = hit.CharacterGUID,
                ItemInstanceID = -1,
                ItemSlotsGUID = null
            };
            ulong queuedInteractionGUID = InteractionManager.Instance.AddToInteractionQueueOfSelectedCharactersFromPlayerInput(
                player.PlayerIndex,
                interactionGUID,
                targetData,
                lotGUID);

            var group = TryRefreshConversationGroupFromQueue(selectedGUID, queuedInteractionGUID);

            _conversationTargetGUID = hit.CharacterGUID;
            _conversationSocialGroupGUID = group?.GUID ?? 0UL;
            _conversationInteractionGUID = queuedInteractionGUID;
            _conversationHasObservedRunning = false;
            _conversationState = ConversationState.Talking;
            _cursorMode = false;
            _isTraversingPath = false;
            _togetherCardSelectedIndex = -1;
            // We stop UpdateHover from running during the conversation (UpdateHoverConversationPatch),
            // so clear any lingering hover highlight now, otherwise it would stay stuck on.
            ClearAllCharacterHover();
            ClearHoveredTogetherCard();

            if (_conversationDialog == null)
                _conversationDialog = new ParaConversationDialog();
            _conversationDialog.Show(speaker, target);
            _conversationDialog.SetTalking(GetCharacterDisplayName(target), group?.TogetherEnergy ?? 0f);
            return true;
        }

        private void HandleConversation()
        {
            _isTraversingPath = false;

            var speaker = GetFollowedCharacterAsset();
            var target = AssetManager.Instance.GetCharacter(_conversationTargetGUID);
            if (speaker == null || target == null)
            {
                EndConversation(cancelInteraction: false);
                return;
            }

            if (IsPauseMenuVisible())
            {
                // Free the cursor so the pause menu is clickable while we hide our own UI.
                // The card view is a separate canvas from the dialog, so it must be hidden
                // explicitly or it (and the quest banner) would float behind the pause menu.
                _cursorMode = true;
                ReleaseLookCursorLock();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _conversationDialog?.SetVisible(false);
                _cardsView?.SetVisible(false);
                return;
            }

            // Keep the player locked in look mode facing the Para for the whole
            // conversation; everything below is driven by the keyboard.
            _cursorMode = false;
            ForceLookCursorLock();
            _conversationDialog?.SetVisible(true);

            // During target-character selection, Q means "back to the card list" (handled in
            // HandleTargetSelectionKeyboard), not "end the whole conversation".
            bool inTargetSelection = _conversationState == ConversationState.ChoicesOpen &&
                                     UI.GetOrNull<UICharacters>(0)?.IsInTargetCharacterSelectionMode == true;
            if (Input.GetKeyDown(KeyCode.Q) && !inTargetSelection)
            {
                EndConversation(cancelInteraction: true);
                return;
            }

            var group = GetConversationGroup(speaker.GUID, target.GUID);

            if (group == null)
            {
                if (!IsConversationInteractionAlive())
                {
                    EndConversation(cancelInteraction: false);
                    return;
                }

                _conversationDialog?.SetApproaching(GetCharacterDisplayName(target), 0f);
                return;
            }

            if (_conversationState != ConversationState.ChoicesOpen && !IsConversationInteractionAlive())
            {
                EndConversation(cancelInteraction: false);
                return;
            }

            bool runningConversation = SocialGroupManager.GetGroupCharactersInConversation(group).Count > 1;
            _conversationHasObservedRunning |= runningConversation;
            if (_conversationHasObservedRunning && !IsConversationInteractionAlive())
            {
                EndConversation(cancelInteraction: false);
                return;
            }
            if (runningConversation)
                FaceConversationPair(speaker, target);

            var uiCharacters = UI.GetOrNull<UICharacters>(0);
            bool togetherMode = uiCharacters != null && (uiCharacters.IsTogetherCardsMode || uiCharacters.IsInTargetCharacterSelectionMode || uiCharacters.IsShowingOutcomes);

            if (_conversationState == ConversationState.ChoicesOpen)
            {
                if (uiCharacters != null && uiCharacters.IsInTargetCharacterSelectionMode)
                {
                    HandleTargetSelectionKeyboard(uiCharacters);
                    UpdateTogetherCardsView(uiCharacters, group);
                    return;
                }

                _targetSelectedIndex = -1;
                // While the vanilla outcome animation plays the player can't pick a card,
                // so show a neutral "resolving" prompt instead of the misleading "choose" one.
                if (uiCharacters != null && uiCharacters.IsShowingOutcomes)
                    _conversationDialog?.SetResolving();
                else
                    _conversationDialog?.SetChoosing(GetCharacterDisplayName(speaker), group.TogetherEnergy);
                HandleTogetherCardsKeyboard(uiCharacters);
                UpdateTogetherCardsView(uiCharacters, group);

                if (!togetherMode || group.TogetherEnergy < 1f)
                {
                    _conversationState = ConversationState.Talking;
                    _togetherCardSelectedIndex = -1;
                    ClearHoveredTogetherCard();
                    HideTogetherCardsView(uiCharacters);
                    if (uiCharacters != null && group.TogetherEnergy < 1f && (uiCharacters.IsTogetherCardsMode || uiCharacters.IsShowingOutcomes))
                        uiCharacters.CloseTogetherResults();
                }
                return;
            }

            if (runningConversation)
                _conversationDialog?.SetTalking(GetConversationLineName(group, target), group.TogetherEnergy);
            else
                _conversationDialog?.SetApproaching(GetConversationLineName(group, target), group.TogetherEnergy);

            if (group.TogetherEnergy >= 1f)
            {
                _conversationDialog?.SetReady(GetConversationLineName(group, target));
                if (Input.GetKeyDown(KeyCode.E))
                {
                    if (OpenTogetherChoices(group))
                    {
                        _conversationState = ConversationState.ChoicesOpen;
                        _togetherCardSelectedIndex = 0;
                    }
                }
            }
        }

        private bool TryFindConversationInteraction(Player player, CenterInteractionHit hit, ulong selectedGUID,
                                                    out ulong interactionGUID, out ulong lotGUID)
        {
            interactionGUID = 0UL;
            lotGUID = LotManager.Instance != null ? LotManager.Instance.GetLotFromPosition(hit.WorldPosition) : 0UL;

            var interactions = Settings.Get<Interactions>();
            if (interactions == null)
                return false;

            ulong groupGUID = InteractionManager.Instance.GetInteractionGroupGUIDFor(interactions, player.SelectedCharactersGUID, hit.CharacterGUID);
            var group = interactions.GetInteractionGroupByGUID(groupGUID);
            if (group == null)
                return false;

            return TryFindConversationInteractionInGroup(group, selectedGUID, hit.CharacterGUID, lotGUID, new HashSet<ulong>(), out interactionGUID);
        }

        private bool TryFindConversationInteractionInGroup(InteractionGroup group, ulong selectedGUID, ulong targetGUID,
                                                          ulong lotGUID, HashSet<ulong> visitedGroups,
                                                          out ulong interactionGUID)
        {
            interactionGUID = 0UL;
            if (group == null || !visitedGroups.Add(group.GUID))
                return false;

            var interactions = Settings.Get<Interactions>();
            ulong[] involved = { selectedGUID, targetGUID };
            foreach (var child in group.ChildrenInteractionAndGroups)
            {
                if (child.Type == InteractionItemType.Interaction)
                {
                    var interaction = interactions.GetInteractionByGUID(child.Interaction);
                    if (IsUsableConversationInteraction(interaction, involved, selectedGUID, targetGUID, lotGUID))
                    {
                        interactionGUID = child.Interaction;
                        return true;
                    }
                    continue;
                }

                var childGroup = interactions.GetInteractionGroupByGUID(child.Group);
                if (childGroup == null)
                    continue;
                if (!InteractionManager.Instance.CanShowInteractionGroupInInteractionList(childGroup, null, 0, selectedGUID, targetGUID, lotGUID))
                    continue;
                if (TryFindConversationInteractionInGroup(childGroup, selectedGUID, targetGUID, lotGUID, visitedGroups, out interactionGUID))
                    return true;
            }

            return false;
        }

        private bool IsUsableConversationInteraction(InteractionUnit interaction, ulong[] involved,
                                                     ulong selectedGUID, ulong targetGUID, ulong lotGUID)
        {
            if (interaction == null || !ActionContainsConversation(interaction.ActionGUID, new HashSet<ulong>()))
                return false;
            if (!CharacterManager.Instance.AnyCharacterHasCharacterRequirement(involved, interaction.CharacterRequirement))
                return false;

            var checkData = InteractionManager.Instance.CreateInteractionRequirementsCheckData(selectedGUID, targetGUID, -1, lotGUID);
            if (!InteractionManager.Instance.CanCharacterDoInteraction(interaction, checkData, CanCharacterDoInteractionState.QueuingInteraction))
                return false;

            var failedRules = InteractionManager.Instance.GetFaillingInteractionUsabilityRule(
                interaction,
                null,
                0,
                selectedGUID,
                targetGUID,
                lotGUID);
            return failedRules.Count == 0;
        }

        private bool ActionContainsConversation(ulong actionGUID, HashSet<ulong> visitedActions)
        {
            if (actionGUID == 0UL || !visitedActions.Add(actionGUID))
                return false;

            var action = Settings.Get<Actions>().GetActionByGUID(actionGUID);
            if (action == null)
                return false;
            if (action.HasConversation)
                return true;
            if (action.Items == null)
                return false;

            foreach (var item in action.Items)
            {
                if (item != null && ActionContainsConversation(item.ActionUnit, visitedActions))
                    return true;
            }
            return false;
        }

        private SocialGroup TryRefreshConversationGroupFromQueue(ulong selectedGUID, ulong interactionGUID)
        {
            if (interactionGUID == 0UL)
                return null;

            var interaction = FindConversationInteractionData(selectedGUID, interactionGUID);
            if (interaction == null || interaction.SocialGroupGUID == 0UL)
                return null;

            _conversationSocialGroupGUID = interaction.SocialGroupGUID;
            return SocialGroupManager.Instance.GetSocialGroupByGUID(interaction.SocialGroupGUID);
        }

        private bool IsConversationInteractionAlive()
        {
            if (_conversationInteractionGUID == 0UL)
                return false;

            var speaker = GetFollowedCharacterAsset();
            if (speaker != null && FindConversationInteractionData(speaker.GUID, _conversationInteractionGUID) != null)
                return true;
            if (_conversationTargetGUID != 0UL && FindConversationInteractionData(_conversationTargetGUID, _conversationInteractionGUID) != null)
                return true;
            return false;
        }

        private AssetCharacterDataInteraction FindConversationInteractionData(ulong characterGUID, ulong interactionGUID)
        {
            var character = AssetManager.Instance.GetCharacter(characterGUID);
            var queue = character?.Data?.CurrentInteractionsInQueue;
            if (queue == null)
                return null;

            for (int i = 0; i < queue.Count; i++)
            {
                var interaction = queue[i];
                if (interaction.GUID != interactionGUID)
                    continue;
                if (interaction.State == AssetCharacterDataInteractionState.ToBeCanceled ||
                    interaction.State == AssetCharacterDataInteractionState.ToBeDeleted ||
                    interaction.State == AssetCharacterDataInteractionState.Cancelling)
                {
                    return null;
                }
                return interaction;
            }
            return null;
        }

        private SocialGroup GetConversationGroup(ulong speakerGUID, ulong targetGUID)
        {
            var group = SocialGroupManager.Instance.GetSocialGroupByGUID(_conversationSocialGroupGUID);
            if (ConversationGroupMatches(group, speakerGUID, targetGUID))
                return group;

            group = TryRefreshConversationGroupFromQueue(speakerGUID, _conversationInteractionGUID);
            if (ConversationGroupMatches(group, speakerGUID, targetGUID))
                return group;

            group = SocialGroupManager.Instance.GetCharacterCurrentSocialGroup(speakerGUID);
            if (ConversationGroupMatches(group, speakerGUID, targetGUID))
            {
                _conversationSocialGroupGUID = group.GUID;
                return group;
            }

            group = SocialGroupManager.Instance.GetCharacterCurrentSocialGroup(targetGUID);
            if (ConversationGroupMatches(group, speakerGUID, targetGUID))
            {
                _conversationSocialGroupGUID = group.GUID;
                return group;
            }

            return null;
        }

        private bool ConversationGroupMatches(SocialGroup group, ulong speakerGUID, ulong targetGUID)
        {
            return group != null &&
                   group.CharactersInGroup.Contains(speakerGUID) &&
                   group.CharactersInGroup.Contains(targetGUID);
        }

        private void FaceConversationPair(AssetCharacter speaker, AssetCharacter target)
        {
            if (speaker == null || target == null)
                return;

            FaceCharacterToward(speaker, target.Data.Position);
            FaceCharacterToward(target, speaker.Data.Position);
            _yaw = speaker.Data.Rotation.eulerAngles.y;
        }

        private void ClearAllCharacterHover()
        {
            if (CharacterManager.Instance == null)
                return;

            foreach (var visual in CharacterManager.Instance.LoadedCharacterVisuals.Values)
                visual?.SetHovered(false);
        }

        private void FaceCharacterToward(AssetCharacter character, Vector3 lookAt)
        {
            var forward = lookAt - character.Data.Position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return;

            character.Data.Rotation = Quaternion.LookRotation(forward.normalized);
        }

        private bool OpenTogetherChoices(SocialGroup group)
        {
            if (group == null || group.TogetherEnergy < 1f)
                return false;

            var uiCharacters = UI.GetOrNull<UICharacters>(0);
            if (uiCharacters == null)
                return false;
            if (uiCharacters.IsTogetherCardsMode)
                return true;

            var player = GetPlayer();
            if (player != null && player.State != GameStates.LiveMode)
            {
                SystemManager.Instance.RegisterMessage(new MessageSetPlayerLiveMode
                {
                    PlayerIndex = 0
                });
            }

            Settings.Get<Audio>().LiveMode.AudioTogetherCardsMenuOpen.Play();
            group.NPCInitiative = TogetherManager.Instance.PickNPCInitiative(group);
            group.CharacterChoices = TogetherManager.Instance.PickCharacterCards(group);
            group.StorytellerChoices = TogetherManager.Instance.PickStorytellerCards(group);
            uiCharacters.SetTogetherCardsMode(group);
            return true;
        }

        private void HandleTogetherCardsKeyboard(UICharacters uiCharacters)
        {
            if (uiCharacters == null || !uiCharacters.IsTogetherCardsMode || uiCharacters.IsInTargetCharacterSelectionMode)
                return;

            var cards = GetVisibleTogetherCards(uiCharacters);
            if (cards.Count == 0)
                return;

            if (_togetherCardSelectedIndex < 0 || _togetherCardSelectedIndex >= cards.Count)
                _togetherCardSelectedIndex = 0;

            bool changed = false;
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) ||
                Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S))
            {
                _togetherCardSelectedIndex = (_togetherCardSelectedIndex + 1) % cards.Count;
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) ||
                Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
            {
                _togetherCardSelectedIndex = (_togetherCardSelectedIndex - 1 + cards.Count) % cards.Count;
                changed = true;
            }

            if (changed || _lastHoveredTogetherCard == null)
                HighlightTogetherCard(cards[_togetherCardSelectedIndex]);

            // R cycles the card's variant (mirrors the in-card "change variant" button).
            if (Input.GetKeyDown(KeyCode.R))
            {
                var selected = cards[_togetherCardSelectedIndex];
                if (selected != null && selected.ChangeVariantButton != null && selected.ChangeVariantButton.activeSelf)
                    selected.SwitchVariant();
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E))
                ClickTogetherCard(cards[_togetherCardSelectedIndex]);
        }

        // Some together cards (e.g. "introduce to") must target another Para before they
        // resolve. The game drops into IsInTargetCharacterSelectionMode and normally waits for
        // the player to click a character portrait — impossible in look mode, where the cursor
        // is locked to screen center. This drives that selection from the keyboard instead:
        // arrows/WASD cycle the eligible Paras, E confirms, Q cancels back to the card list.
        private void HandleTargetSelectionKeyboard(UICharacters uiCharacters)
        {
            BuildTargetCandidates(uiCharacters);

            if (_targetCandidateBuffer.Count == 0)
            {
                // Nothing selectable (shouldn't happen) — let the player back out.
                if (Input.GetKeyDown(KeyCode.Q))
                    uiCharacters.OnCancelCharacterSelection();
                return;
            }

            if (_targetSelectedIndex < 0 || _targetSelectedIndex >= _targetCandidateBuffer.Count)
                _targetSelectedIndex = 0;

            bool changed = false;
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) ||
                Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S))
            {
                _targetSelectedIndex = (_targetSelectedIndex + 1) % _targetCandidateBuffer.Count;
                changed = true;
            }
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) ||
                Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
            {
                _targetSelectedIndex = (_targetSelectedIndex - 1 + _targetCandidateBuffer.Count) % _targetCandidateBuffer.Count;
                changed = true;
            }

            ulong selectedGUID = _targetCandidateBuffer[_targetSelectedIndex];
            if (changed || _lastHighlightedCharacterItem == null)
                HighlightCharacterItem(uiCharacters, selectedGUID);

            var candidate = CharacterManager.Instance != null
                ? CharacterManager.Instance.GetCharacterByGUID(selectedGUID)
                : null;
            _conversationDialog?.SetTargeting(GetCharacterDisplayName(candidate));

            if (Input.GetKeyDown(KeyCode.Q))
            {
                ClearHighlightedCharacterItem();
                uiCharacters.OnCancelCharacterSelection();
                _targetSelectedIndex = -1;
                return;
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E))
            {
                ClearHighlightedCharacterItem();
                uiCharacters.OnCharacterSelection(selectedGUID);
                _targetSelectedIndex = -1;
            }
        }

        // Reads the game's private list of targetable characters for the active selection
        // prompt. For together-card targeting the "cannot be selected" list is always empty,
        // so the can-be-selected list is the authoritative candidate set.
        private void BuildTargetCandidates(UICharacters uiCharacters)
        {
            _targetCandidateBuffer.Clear();
            if (_canBeSelectedField == null)
            {
                _canBeSelectedField = typeof(UICharacters).GetField(
                    "_charactersThatCanBeSelected",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_canBeSelectedField?.GetValue(uiCharacters) is List<ulong> candidates)
            {
                foreach (var guid in candidates)
                    if (guid != 0UL)
                        _targetCandidateBuffer.Add(guid);
            }
        }

        // Highlights a character portrait the same way a mouse hover would, so the vanilla
        // target-selection UI visibly tracks the keyboard cursor.
        private void HighlightCharacterItem(UICharacters uiCharacters, ulong guid)
        {
            if (uiCharacters?.List == null || EventSystem.current == null)
                return;

            ClearHighlightedCharacterItem();
            foreach (UIListItemBase item in uiCharacters.List.CurrentItems)
            {
                if (!(item is UICharacterItem characterItem) || characterItem.Character == null)
                    continue;
                if (characterItem.Character.GUID != guid)
                    continue;

                var eventData = new PointerEventData(EventSystem.current)
                {
                    position = characterItem.GetComponent<RectTransform>().position
                };
                ExecuteEvents.Execute(characterItem.gameObject, eventData, ExecuteEvents.pointerEnterHandler);
                EventSystem.current.SetSelectedGameObject(characterItem.gameObject);
                _lastHighlightedCharacterItem = characterItem.gameObject;
                return;
            }
        }

        private void ClearHighlightedCharacterItem()
        {
            if (_lastHighlightedCharacterItem != null && EventSystem.current != null)
            {
                var eventData = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(_lastHighlightedCharacterItem, eventData, ExecuteEvents.pointerExitHandler);
            }
            _lastHighlightedCharacterItem = null;
        }

        /// <summary>
        /// Drives the in-dialog (ParaWASD-styled) together-cards view: mirrors the live
        /// card data into our own canvas and hides the vanilla card chrome. The outcome
        /// reveal still uses the stock UI, so we reveal the vanilla chrome and hide our
        /// view during that sub-phase. Target-character selection keeps the vanilla
        /// chrome visible (its dimmed portrait picker) while we drive it from the keyboard.
        /// </summary>
        private void UpdateTogetherCardsView(UICharacters uiCharacters, SocialGroup group)
        {
            if (_cardsView == null)
                _cardsView = new ParaTogetherCardsView();

            // Target selection: hide the vanilla cards (and block their raycasts so nothing can
            // be silently clicked) but keep the dimmed backdrop up for the portrait picker.
            // We drive the pick from the keyboard, so our card view stays hidden here.
            if (uiCharacters != null && uiCharacters.IsTogetherCardsMode && uiCharacters.IsInTargetCharacterSelectionMode)
            {
                ApplyVanillaChromeState(uiCharacters, VanillaChromeState.TargetPicker);
                _cardsView.SetVisible(false);
                return;
            }

            bool deferToVanilla = uiCharacters == null || !uiCharacters.IsTogetherCardsMode ||
                                  uiCharacters.IsShowingOutcomes;
            if (deferToVanilla)
            {
                ApplyVanillaChromeState(uiCharacters, VanillaChromeState.Shown);
                _cardsView.SetVisible(false);
                return;
            }

            ApplyVanillaChromeState(uiCharacters, VanillaChromeState.Hidden);

            var cards = GetVisibleTogetherCards(uiCharacters);
            _cardDataBuffer.Clear();
            foreach (var card in cards)
                _cardDataBuffer.Add(BuildCardData(card));

            if (_cardDataBuffer.Count == 0)
            {
                _cardsView.SetVisible(false);
                return;
            }

            // Owner labels only add value when more than one Para is offering cards; in a
            // 1:1 chat they're noise, so blank them unless there are at least two owners.
            int distinctOwners = 0;
            string firstOwner = null;
            foreach (var d in _cardDataBuffer)
            {
                if (string.IsNullOrEmpty(d.OwnerName))
                    continue;
                if (firstOwner == null)
                {
                    firstOwner = d.OwnerName;
                    distinctOwners = 1;
                }
                else if (d.OwnerName != firstOwner)
                {
                    distinctOwners = 2;
                    break;
                }
            }
            if (distinctOwners < 2)
            {
                for (int i = 0; i < _cardDataBuffer.Count; i++)
                {
                    var d = _cardDataBuffer[i];
                    d.OwnerName = null;
                    _cardDataBuffer[i] = d;
                }
            }

            if (_togetherCardSelectedIndex < 0 || _togetherCardSelectedIndex >= _cardDataBuffer.Count)
                _togetherCardSelectedIndex = 0;

            _cardsView.Sync(_cardDataBuffer, _togetherCardSelectedIndex, BuildInitiativeData(group));
            _cardsView.SetVisible(true);
        }

        private ParaTogetherCardsView.InitiativeData BuildInitiativeData(SocialGroup group)
        {
            var data = new ParaTogetherCardsView.InitiativeData();
            if (group == null || !group.NPCInitiative.hasInitiative)
                return data;

            data.HasInitiative = true;
            var npc = CharacterManager.Instance != null
                ? CharacterManager.Instance.GetCharacterByGUID(group.NPCInitiative.npcGUID)
                : null;
            data.NpcName = GetCharacterDisplayName(npc);
            data.NpcThumbnail = npc != null ? npc.ThumbnailSprite : null;
            data.BackgroundColor = ParaThumbnail.ResolveColor(npc, new Color(0.80f, 0.84f, 0.86f, 1f));

            var initiative = group.NPCInitiative.initiative;
            if (initiative.RequestGUID != 0UL)
            {
                data.IsQuest = true;
                var goal = Settings.Get<Goals>().GetGoalByGUID(initiative.RequestGUID);
                data.RequestText = goal != null ? TranslationManager.Get("GoalDescription_" + goal.Description) : string.Empty;
            }
            else
            {
                var card = Settings.Get<Together>().GetCardByGUID(initiative.CardGUID);
                data.RequestText = card != null ? TranslationManager.Get("TogetherCard_" + card.DisplayName) : string.Empty;
            }
            return data;
        }

        private void HideTogetherCardsView(UICharacters uiCharacters)
        {
            _cardsView?.SetVisible(false);
            ApplyVanillaChromeState(uiCharacters, VanillaChromeState.Shown);
        }

        private ParaTogetherCardsView.CardData BuildCardData(UITogetherCard card)
        {
            var data = new ParaTogetherCardsView.CardData { Source = card };
            if (card == null)
                return data;

            if (card.Label != null && card.Label.Text != null)
            {
                data.Title = card.Label.Text.text;
                data.TextColor = card.Label.Text.color;
            }
            data.BackgroundColor = card.ImageBackground != null ? card.ImageBackground.color : Color.white;

            data.HasSuccessChance = card.SuccessChanceContainer != null && card.SuccessChanceContainer.activeSelf;
            if (card.SuccessChanceLabel != null)
                data.SuccessText = card.SuccessChanceLabel.text;

            data.HasTrait = card.ImageIconPersonality != null && card.ImageIconPersonality.gameObject.activeSelf;
            data.TraitIcon = card.ImageIconPersonality != null ? card.ImageIconPersonality.sprite : null;
            if (card.LabelPersonalityTrait != null && card.LabelPersonalityTrait.Text != null)
                data.TraitLabel = card.LabelPersonalityTrait.Text.text;

            data.HasItem = card.ImageInventoryItem != null && card.ImageInventoryItem.gameObject.activeSelf;
            data.ItemIcon = card.ImageInventoryItem != null ? card.ImageInventoryItem.sprite : null;

            data.HasVariant = card.ChangeVariantButton != null && card.ChangeVariantButton.activeSelf;

            data.HasRelationship = card.RelationshipLabelContainer != null && card.RelationshipLabelContainer.activeSelf;
            if (card.RelationshipLabelText != null)
                data.RelationshipText = card.RelationshipLabelText.text;

            var owner = card.GetComponentInParent<UICharacterItem>();
            if (owner != null && owner.Character != null)
                data.OwnerName = GetCharacterDisplayName(owner.Character);
            return data;
        }

        // Applies one of three vanilla-chrome visibility configurations. Reapplied every frame so
        // cards that spawn after the state was set still get hidden and de-raycasted — otherwise a
        // card sitting invisibly under the target picker could still be clicked. Cards use a
        // CanvasGroup (alpha + interactable + blocksRaycasts), not just Canvas.alpha, so an
        // invisible card can't be selected.
        private void ApplyVanillaChromeState(UICharacters uiCharacters, VanillaChromeState state)
        {
            if (uiCharacters == null)
                return;

            bool hideCards = state != VanillaChromeState.Shown;
            bool showDimmed = state != VanillaChromeState.Hidden;

            foreach (var card in GetVisibleTogetherCards(uiCharacters))
                SetGameObjectHidden(card.gameObject, hideCards);

            SetGameObjectHidden(uiCharacters.DimmedBackground, !showDimmed);
            SetGameObjectHidden(uiCharacters.TogetherCardStorytellerPanel, hideCards);
            SetGameObjectHidden(uiCharacters.NPCInitiatives, hideCards);
            SetGameObjectHidden(uiCharacters.TogetherCardsOKButton, hideCards);
        }

        private static void SetGameObjectHidden(GameObject go, bool hidden)
        {
            if (go == null)
                return;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                if (!hidden)
                    return;
                cg = go.AddComponent<CanvasGroup>();
            }
            cg.alpha = hidden ? 0f : 1f;
            cg.interactable = !hidden;
            cg.blocksRaycasts = !hidden;
        }

        private List<UITogetherCard> GetVisibleTogetherCards(UICharacters uiCharacters)
        {
            var cards = new List<UITogetherCard>();
            if (uiCharacters?.StorytellerOfferedCards?.TogetherCardsList != null)
                AddCardsFromList(uiCharacters.StorytellerOfferedCards.TogetherCardsList, cards);

            if (uiCharacters?.List != null)
            {
                foreach (UIListItemBase item in uiCharacters.List.CurrentItems)
                {
                    if (item is UICharacterItem characterItem && characterItem.OfferedTogetherCards?.TogetherCardsList != null)
                        AddCardsFromList(characterItem.OfferedTogetherCards.TogetherCardsList, cards);
                }
            }
            return cards;
        }

        private void AddCardsFromList(UIList list, List<UITogetherCard> cards)
        {
            foreach (UIListItemBase item in list.CurrentItems)
            {
                if (item is UITogetherCard card && card.gameObject.activeInHierarchy && card.Interactable)
                    cards.Add(card);
            }
        }

        private void HighlightTogetherCard(UITogetherCard card)
        {
            if (card == null || EventSystem.current == null)
                return;

            ClearHoveredTogetherCard();
            var eventData = new PointerEventData(EventSystem.current)
            {
                position = card.GetComponent<RectTransform>().position
            };
            ExecuteEvents.Execute(card.gameObject, eventData, ExecuteEvents.pointerEnterHandler);
            EventSystem.current.SetSelectedGameObject(card.gameObject);
            _lastHoveredTogetherCard = card;
        }

        private void ClearHoveredTogetherCard()
        {
            if (_lastHoveredTogetherCard != null && (UnityEngine.Object)_lastHoveredTogetherCard != null &&
                _lastHoveredTogetherCard.gameObject != null && EventSystem.current != null)
            {
                var eventData = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(_lastHoveredTogetherCard.gameObject, eventData, ExecuteEvents.pointerExitHandler);
            }
            _lastHoveredTogetherCard = null;
        }

        private void ClickTogetherCard(UITogetherCard card)
        {
            if (card == null || EventSystem.current == null)
                return;

            var eventData = new PointerEventData(EventSystem.current);
            ExecuteEvents.Execute(card.gameObject, eventData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(card.gameObject, eventData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(card.gameObject, eventData, ExecuteEvents.pointerClickHandler);
        }

        private void EndConversation(bool cancelInteraction)
        {
            if (!IsConversationActive)
                return;

            if (cancelInteraction)
                CancelConversationInteraction();

            var uiCharacters = UI.GetOrNull<UICharacters>(0);
            uiCharacters?.OnCancelCharacterSelection();
            uiCharacters?.CloseTogetherResults();

            ClearHoveredTogetherCard();
            ClearHighlightedCharacterItem();
            HideTogetherCardsView(uiCharacters);
            _conversationDialog?.Hide();

            _conversationState = ConversationState.None;
            _conversationTargetGUID = 0UL;
            _conversationSocialGroupGUID = 0UL;
            _conversationInteractionGUID = 0UL;
            _conversationHasObservedRunning = false;
            _togetherCardSelectedIndex = -1;
            _cursorMode = false;
            _snapCameraNextFrame = true;
        }

        private void CancelConversationInteraction()
        {
            if (_conversationInteractionGUID == 0UL)
                return;

            var speakerInteraction = FindConversationInteractionData(_followedCharacterGUID, _conversationInteractionGUID);
            if (speakerInteraction != null)
            {
                InteractionManager.Instance.CancelInteraction(speakerInteraction);
                return;
            }

            var targetInteraction = FindConversationInteractionData(_conversationTargetGUID, _conversationInteractionGUID);
            if (targetInteraction != null)
                InteractionManager.Instance.CancelInteraction(targetInteraction);
        }

        private string GetCharacterDisplayName(AssetCharacter character)
        {
            if (character == null || character.Data == null)
                return "Para";
            string name = character.Data.ShortName;
            return string.IsNullOrWhiteSpace(name) ? character.Data.FullName : name;
        }

        private string GetConversationLineName(SocialGroup group, AssetCharacter target)
        {
            if (group != null && group.TalkerCharacter == _conversationTargetGUID)
            {
                var talker = AssetManager.Instance.GetCharacter(group.TalkerCharacter);
                if (talker != null)
                    return GetCharacterDisplayName(talker);
            }

            return GetCharacterDisplayName(target);
        }

        private bool IsPauseMenuVisible()
        {
            var pauseMenu = UI.GetOrNull<UIEscapeMenu>(0);
            return pauseMenu != null && pauseMenu.IsVisible;
        }

        private void HandlePauseCursorMode()
        {
            bool pauseVisible = IsPauseMenuVisible();
            if (Input.GetKeyDown(KeyCode.Escape) && !pauseVisible && !IsConversationActive)
            {
                _cursorModeBeforePause = _cursorMode;
                _pauseForcedCursorMode = !_cursorMode;
                _cursorMode = true;
                _isTraversingPath = false;
                ReleaseLookCursorLock();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (pauseVisible && !_pauseMenuWasVisible && !_pauseForcedCursorMode && !IsConversationActive)
            {
                _cursorModeBeforePause = _cursorMode;
                _pauseForcedCursorMode = !_cursorMode;
                _cursorMode = true;
            }
            else if (!pauseVisible && _pauseMenuWasVisible && _pauseForcedCursorMode && !IsConversationActive)
            {
                _cursorMode = _cursorModeBeforePause;
                _pauseForcedCursorMode = false;
            }

            _pauseMenuWasVisible = pauseVisible;
        }

        private Player GetPlayer()
        {
            if (PlayerManager.Instance == null || PlayerManager.Instance.HybridPlayer1 == null)
                return null;
            return PlayerManager.Instance.HybridPlayer1.Player;
        }

        private struct CenterInteractionHit
        {
            public bool HasHit;
            public ColliderType ColliderType;
            public GameObject RaycastObject;
            public Vector3 WorldPosition;
            public ulong CharacterGUID;
        }

        private enum ConversationState
        {
            None,
            Talking,
            ChoicesOpen
        }

        private int GetFullAreaMask()
        {
            if (UnityLayersManager.Instance != null)
            {
                var areas = UnityLayersManager.Instance.NavmeshArea;
                return (1 << areas.Floor) | (1 << areas.Terrain) | (1 << areas.Walkable);
            }
            return NavMesh.AllAreas;
        }

        private void CancelCharacterActions()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null) return;
            var interactions = characterAsset.Data.CurrentInteractionsInQueue;
            if (interactions != null && InteractionManager.Instance != null)
            {
                for (int i = interactions.Count - 1; i >= 0; i--)
                    InteractionManager.Instance.CancelInteraction(interactions[i]);
            }
            if (characterAsset.Data.PathfindingData != null)
                PathfindingManager.Instance.SetCharacterPathfindingDataToNull(characterAsset);
        }

        private void TryCancelCurrentAction()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset == null || InteractionManager.Instance == null)
                return;

            var interactions = characterAsset.Data.CurrentInteractionsInQueue;
            if (interactions == null)
                return;

            for (int i = 0; i < interactions.Count; i++)
            {
                var interaction = interactions[i];
                if (interaction.State == AssetCharacterDataInteractionState.ToBeCanceled ||
                    interaction.State == AssetCharacterDataInteractionState.ToBeDeleted ||
                    interaction.State == AssetCharacterDataInteractionState.Cancelling)
                {
                    continue;
                }

                InteractionManager.Instance.CancelInteraction(interaction);
                _isTraversingPath = false;
                return;
            }
        }

        private void UpdateCameraPosition()
        {
            if (_gameCamera == null || _headBone == null) return;

            Quaternion targetRotation = Quaternion.Euler(_pitch, _yaw, 0);
            Vector3 targetPosition = _headBone.position
                + Vector3.up * GetEyeHeightOffset()
                + GetCharacterForward() * Plugin.ForwardOffset.Value;

            float smoothing = Plugin.CameraSmoothing.Value;
            if (_snapCameraNextFrame || smoothing <= 0f)
            {
                _gameCamera.transform.position = targetPosition;
                _gameCamera.transform.rotation = targetRotation;
                _snapCameraNextFrame = false;
            }
            else
            {
                float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
                _gameCamera.transform.position = Vector3.Lerp(_gameCamera.transform.position, targetPosition, t);
                _gameCamera.transform.rotation = Quaternion.Slerp(_gameCamera.transform.rotation, targetRotation, t);
            }
            _gameCamera.fieldOfView = Plugin.FieldOfView.Value;
        }

        private float GetMoveSpeed()
        {
            float speed = Plugin.MoveSpeed.Value;
            if (Input.GetKey(KeyCode.LeftShift))
                speed *= Plugin.SprintMultiplier.Value;
            return speed;
        }

        private float GetEyeHeightOffset()
        {
            return _usingFallbackHeadTransform
                ? Plugin.FallbackEyeHeightOffset.Value
                : Plugin.EyeHeightOffset.Value;
        }

        private AssetCharacter GetFollowedCharacterAsset()
        {
            if (CharacterManager.Instance == null) return null;
            for (int i = 0; i < CharacterManager.Instance.Characters.Count; i++)
            {
                if (CharacterManager.Instance.Characters[i].GUID == _followedCharacterGUID)
                    return CharacterManager.Instance.Characters[i];
            }
            return null;
        }

        private Vector3 GetCharacterForward()
        {
            var characterAsset = GetFollowedCharacterAsset();
            if (characterAsset != null)
                return characterAsset.Data.Rotation * Vector3.forward;
            return _headBone != null ? _headBone.forward : Vector3.forward;
        }

        private void SaveCameraState()
        {
            if (_gameCamera == null) return;
            _savedCameraPosition = _gameCamera.transform.position;
            _savedCameraRotation = _gameCamera.transform.rotation;
            _savedCameraFOV = _gameCamera.fieldOfView;
            _savedNearClip = _gameCamera.nearClipPlane;
        }

        private void RestoreCameraState()
        {
            if (_gameCamera == null) return;
            _gameCamera.transform.position = _savedCameraPosition;
            _gameCamera.transform.rotation = _savedCameraRotation;
            _gameCamera.fieldOfView = _savedCameraFOV;
            _gameCamera.nearClipPlane = _savedNearClip;
        }

        // Show/hide the followed Para's body for first person. When hiding with ShowSelfShadow on,
        // render the body shadow-only: it never draws into the view but still casts a shadow on the
        // floor/walls (looking down at no shadow looks like floating), with no camera clipping. With
        // the toggle off, disable the renderers outright. Showing restores normal shadow casting.
        private void SetCharacterVisible(bool visible)
        {
            if (_followedVisual == null) return;
            bool shadowOnly = !visible && Plugin.ShowSelfShadow.Value;
            var renderers = _followedVisual.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (visible)
                {
                    r.enabled = true;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                else if (shadowOnly)
                {
                    r.enabled = true;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
                else
                {
                    r.enabled = false;
                }
            }
        }
    }

    internal sealed class ParaConversationDialog
    {
        private const int SortingOrder = 32000;

        // Paralives palette: cream rounded cards, soft slate text, signature teal accent.
        private static readonly Color PanelColor = new Color(0.98f, 0.97f, 0.93f, 0.98f);
        private static readonly Color AccentColor = new Color(0.27f, 0.78f, 0.82f, 1f);
        private static readonly Color FrameColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color FrameBorderColor = new Color(0.80f, 0.84f, 0.86f, 1f);
        private static readonly Color TitleColor = new Color(0.16f, 0.19f, 0.24f, 1f);
        private static readonly Color BodyColor = new Color(0.32f, 0.36f, 0.42f, 1f);
        private static readonly Color TrackColor = new Color(0.84f, 0.86f, 0.87f, 1f);
        private static readonly Color FillColor = new Color(0.27f, 0.78f, 0.82f, 1f);
        private static readonly Color ChipBgColor = new Color(0.93f, 0.94f, 0.92f, 0.95f);
        private static readonly Color KeyBadgeColor = new Color(0.27f, 0.78f, 0.82f, 1f);
        private static readonly Color KeyBadgePulseColor = new Color(0.46f, 0.88f, 0.92f, 1f);
        private static readonly Color KeyTextColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color ChipLabelColor = new Color(0.28f, 0.32f, 0.38f, 1f);

        private readonly GameObject _root;
        private readonly Image _portrait;
        private readonly Image _portraitBackground;
        private readonly Text _title;
        private readonly Text _body;
        private readonly RectTransform _fillRect;

        private Font _font;
        private Sprite _rounded;
        private AssetCharacter _target;
        private RectTransform _promptRow;
        private readonly List<PromptChip> _chips = new List<PromptChip>();

        public ParaConversationDialog()
        {
            _root = new GameObject("ParaWASDConversationDialog");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var font = ParaFonts.GameFont;
            var rounded = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            _font = font;
            _rounded = rounded;

            var shadow = CreateRect("Shadow", _root.transform);
            shadow.anchorMin = new Vector2(0.5f, 0f);
            shadow.anchorMax = new Vector2(0.5f, 0f);
            shadow.pivot = new Vector2(0.5f, 0f);
            shadow.anchoredPosition = new Vector2(0f, 104f);
            shadow.sizeDelta = new Vector2(792f, 250f);
            var shadowImage = shadow.gameObject.AddComponent<Image>();
            shadowImage.sprite = rounded;
            shadowImage.type = Image.Type.Sliced;
            shadowImage.color = new Color(0f, 0f, 0f, 0.22f);

            var panel = CreateRect("Panel", _root.transform);
            panel.anchorMin = new Vector2(0.5f, 0f);
            panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, 110f);
            panel.sizeDelta = new Vector2(760f, 234f);
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.sprite = rounded;
            panelImage.type = Image.Type.Sliced;
            panelImage.color = PanelColor;

            var accent = CreateRect("Accent", panel);
            accent.anchorMin = new Vector2(0f, 1f);
            accent.anchorMax = new Vector2(1f, 1f);
            accent.pivot = new Vector2(0.5f, 1f);
            accent.anchoredPosition = new Vector2(0f, -10f);
            accent.sizeDelta = new Vector2(-32f, 5f);
            var accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.sprite = rounded;
            accentImage.type = Image.Type.Sliced;
            accentImage.color = AccentColor;

            var portraitFrame = CreateRect("PortraitFrame", panel);
            portraitFrame.anchorMin = new Vector2(0f, 0.5f);
            portraitFrame.anchorMax = new Vector2(0f, 0.5f);
            portraitFrame.pivot = new Vector2(0f, 0.5f);
            portraitFrame.anchoredPosition = new Vector2(24f, 0f);
            portraitFrame.sizeDelta = new Vector2(150f, 150f);
            var frameImage = portraitFrame.gameObject.AddComponent<Image>();
            frameImage.sprite = rounded;
            frameImage.type = Image.Type.Sliced;
            frameImage.color = FrameBorderColor;

            var portraitInner = CreateRect("PortraitInner", portraitFrame);
            portraitInner.anchorMin = new Vector2(0f, 0f);
            portraitInner.anchorMax = new Vector2(1f, 1f);
            portraitInner.offsetMin = new Vector2(4f, 4f);
            portraitInner.offsetMax = new Vector2(-4f, -4f);
            var innerImage = portraitInner.gameObject.AddComponent<Image>();
            innerImage.sprite = rounded;
            innerImage.type = Image.Type.Sliced;
            innerImage.color = FrameColor;
            _portraitBackground = innerImage;

            var portraitRect = CreateRect("Portrait", portraitInner);
            portraitRect.anchorMin = new Vector2(0f, 0f);
            portraitRect.anchorMax = new Vector2(1f, 1f);
            portraitRect.offsetMin = new Vector2(5f, 5f);
            portraitRect.offsetMax = new Vector2(-5f, -5f);
            _portrait = portraitRect.gameObject.AddComponent<Image>();
            _portrait.color = Color.white;
            _portrait.preserveAspect = true;

            _title = CreateText("Title", panel, font, 28, FontStyle.Bold, TextAnchor.MiddleLeft);
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0f, 1f);
            _title.rectTransform.anchoredPosition = new Vector2(200f, -34f);
            _title.rectTransform.sizeDelta = new Vector2(-224f, 40f);
            _title.color = TitleColor;

            _body = CreateText("Body", panel, font, 24, FontStyle.Normal, TextAnchor.MiddleLeft);
            _body.rectTransform.anchorMin = new Vector2(0f, 1f);
            _body.rectTransform.anchorMax = new Vector2(1f, 1f);
            _body.rectTransform.pivot = new Vector2(0f, 1f);
            _body.rectTransform.anchoredPosition = new Vector2(200f, -82f);
            _body.rectTransform.sizeDelta = new Vector2(-234f, 48f);
            _body.color = BodyColor;

            var progressBack = CreateRect("ProgressBack", panel);
            progressBack.anchorMin = new Vector2(0f, 0f);
            progressBack.anchorMax = new Vector2(1f, 0f);
            progressBack.pivot = new Vector2(0f, 0f);
            progressBack.anchoredPosition = new Vector2(200f, 70f);
            progressBack.sizeDelta = new Vector2(-234f, 16f);
            var trackImage = progressBack.gameObject.AddComponent<Image>();
            trackImage.sprite = rounded;
            trackImage.type = Image.Type.Sliced;
            trackImage.color = TrackColor;

            // Anchor-driven fill: width = progress * track width. This renders reliably for a
            // runtime-built Image (a Filled-type Image needs a configured sprite to draw its
            // fill, which was why the old meter never appeared).
            _fillRect = CreateRect("ProgressFill", progressBack);
            _fillRect.anchorMin = new Vector2(0f, 0f);
            _fillRect.anchorMax = new Vector2(0f, 1f);
            _fillRect.pivot = new Vector2(0f, 0.5f);
            _fillRect.offsetMin = Vector2.zero;
            _fillRect.offsetMax = Vector2.zero;
            var fillImage = _fillRect.gameObject.AddComponent<Image>();
            fillImage.sprite = rounded;
            fillImage.type = Image.Type.Sliced;
            fillImage.color = FillColor;

            CreatePromptRow(panel);

            SetProgress(0f);
            _root.SetActive(false);
        }

        private void SetProgress(float progress)
        {
            if (_fillRect == null)
                return;
            float clamped = Mathf.Clamp01(progress);
            _fillRect.anchorMax = new Vector2(clamped, 1f);
            _fillRect.offsetMin = Vector2.zero;
            _fillRect.offsetMax = Vector2.zero;
        }

        public void Show(AssetCharacter speaker, AssetCharacter target)
        {
            _root.SetActive(true);
            _target = target;
            _title.text = target != null && target.Data != null ? target.Data.FullName : "Conversation";
            RefreshPortrait();
            SetTalking(GetName(speaker), 0f);
        }

        // The relationship-status background color can change mid-conversation (e.g. the
        // meter shifts the pair from acquaintances to friends), so re-read it every frame
        // from the per-frame Set* calls rather than only at Show().
        private void RefreshPortrait()
        {
            _portrait.sprite = _target != null ? _target.ThumbnailSprite : null;
            if (_portraitBackground != null)
                _portraitBackground.color = ParaThumbnail.ResolveColor(_target, FrameColor);
        }

        public void Hide()
        {
            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            if (_root.activeSelf != visible)
                _root.SetActive(visible);
        }

        public void SetTalking(string speakerName, float progress)
        {
            _body.text = speakerName + " is talking...";
            SetPrompts(new PromptInfo("Q", "End", false));
            SetProgress(progress);
            RefreshPortrait();
        }

        public void SetApproaching(string speakerName, float progress)
        {
            _body.text = speakerName + " is starting a conversation...";
            SetPrompts(new PromptInfo("Q", "End", false));
            SetProgress(progress);
            RefreshPortrait();
        }

        public void SetReady(string speakerName)
        {
            _body.text = speakerName + " is ready to talk!";
            SetPrompts(new PromptInfo("E", "Talk", true), new PromptInfo("Q", "End", false));
            SetProgress(1f);
            RefreshPortrait();
        }

        public void SetChoosing(string speakerName, float progress)
        {
            _body.text = "Choose what " + speakerName + " says next.";
            SetPrompts(new PromptInfo("\u2190 \u2192", "Browse", false), new PromptInfo("E", "Choose", true), new PromptInfo("Q", "End", false));
            SetProgress(progress);
            RefreshPortrait();
        }

        public void SetResolving()
        {
            _body.text = "Resolving...";
            SetPrompts(new PromptInfo("Q", "End", false));
            SetProgress(1f);
            RefreshPortrait();
        }

        public void SetTargeting(string candidateName)
        {
            _body.text = "Choose who to involve: " + candidateName;
            SetPrompts(new PromptInfo("\u2190 \u2192", "Browse", false), new PromptInfo("E", "Confirm", true), new PromptInfo("Q", "Back", false));
            SetProgress(1f);
            RefreshPortrait();
        }

        private void SetPrompts(params PromptInfo[] prompts)
        {
            EnsureChips(prompts.Length);
            for (int i = 0; i < _chips.Count; i++)
            {
                var chip = _chips[i];
                if (i < prompts.Length)
                {
                    var info = prompts[i];
                    chip.Root.SetActive(true);
                    chip.KeyText.text = info.Key;
                    chip.Label.text = info.Label;
                    float keyWidth = Mathf.Max(30f, info.Key.Length * 14f + 16f);
                    chip.KeyLayout.minWidth = keyWidth;
                    chip.KeyLayout.preferredWidth = keyWidth;
                    chip.Pulse.enabled = info.Pulse;
                    if (!info.Pulse)
                    {
                        chip.Root.transform.localScale = Vector3.one;
                        chip.KeyBadge.color = KeyBadgeColor;
                    }
                }
                else
                {
                    chip.Pulse.enabled = false;
                    chip.Root.SetActive(false);
                }
            }
        }

        private void EnsureChips(int count)
        {
            while (_chips.Count < count)
                _chips.Add(CreateChip(_chips.Count));
        }

        private PromptChip CreateChip(int index)
        {
            var root = CreateRect("PromptChip" + index, _promptRow);
            var bg = root.gameObject.AddComponent<Image>();
            bg.sprite = _rounded;
            bg.type = Image.Type.Sliced;
            bg.color = ChipBgColor;

            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(8, 12, 5, 5);
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var badge = CreateRect("KeyBadge", root);
            var badgeImage = badge.gameObject.AddComponent<Image>();
            badgeImage.sprite = _rounded;
            badgeImage.type = Image.Type.Sliced;
            badgeImage.color = KeyBadgeColor;
            var keyLayout = badge.gameObject.AddComponent<LayoutElement>();
            keyLayout.minWidth = 30f;
            keyLayout.preferredWidth = 30f;
            keyLayout.minHeight = 30f;
            keyLayout.preferredHeight = 30f;

            var keyText = CreateText("KeyText", badge, _font, 17, FontStyle.Bold, TextAnchor.MiddleCenter);
            keyText.rectTransform.anchorMin = Vector2.zero;
            keyText.rectTransform.anchorMax = Vector2.one;
            keyText.rectTransform.offsetMin = Vector2.zero;
            keyText.rectTransform.offsetMax = Vector2.zero;
            keyText.color = KeyTextColor;
            keyText.horizontalOverflow = HorizontalWrapMode.Overflow;

            var label = CreateText("Label", root, _font, 18, FontStyle.Bold, TextAnchor.MiddleLeft);
            label.color = ChipLabelColor;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;

            var pulse = root.gameObject.AddComponent<UIPulse>();
            pulse.Target = badgeImage;
            pulse.BaseColor = KeyBadgeColor;
            pulse.PulseColor = KeyBadgePulseColor;
            pulse.enabled = false;

            return new PromptChip
            {
                Root = root.gameObject,
                Rect = root,
                Background = bg,
                KeyBadge = badgeImage,
                KeyText = keyText,
                KeyLayout = keyLayout,
                Label = label,
                Pulse = pulse
            };
        }

        private void CreatePromptRow(RectTransform panel)
        {
            _promptRow = CreateRect("PromptRow", panel);
            _promptRow.anchorMin = new Vector2(0f, 0f);
            _promptRow.anchorMax = new Vector2(0f, 0f);
            _promptRow.pivot = new Vector2(0f, 0f);
            _promptRow.anchoredPosition = new Vector2(200f, 16f);

            var layout = _promptRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = _promptRow.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        private static Text CreateText(string name, Transform parent, Font font, int size, FontStyle style, TextAnchor alignment)
        {
            var rect = CreateRect(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static string GetName(AssetCharacter character)
        {
            if (character == null || character.Data == null)
                return "Para";
            string shortName = character.Data.ShortName;
            return string.IsNullOrWhiteSpace(shortName) ? character.Data.FullName : shortName;
        }

        private readonly struct PromptInfo
        {
            public readonly string Key;
            public readonly string Label;
            public readonly bool Pulse;

            public PromptInfo(string key, string label, bool pulse)
            {
                Key = key;
                Label = label;
                Pulse = pulse;
            }
        }

        private sealed class PromptChip
        {
            public GameObject Root;
            public RectTransform Rect;
            public Image Background;
            public Image KeyBadge;
            public Text KeyText;
            public LayoutElement KeyLayout;
            public Text Label;
            public UIPulse Pulse;
        }
    }

    /// <summary>
    /// Gentle game-style attention pulse for a prompt chip: a soft scale bounce plus an
    /// accent-color shimmer on the key badge. Driven by unscaled time so it animates even
    /// while the conversation pauses gameplay.
    /// </summary>
    internal sealed class UIPulse : MonoBehaviour
    {
        public float Speed = 5f;
        public float ScaleAmount = 0.08f;
        public Graphic Target;
        public Color BaseColor = Color.white;
        public Color PulseColor = Color.white;

        private RectTransform _rect;

        private void Awake() => _rect = GetComponent<RectTransform>();

        private void OnDisable()
        {
            if (_rect != null)
                _rect.localScale = Vector3.one;
            if (Target != null)
                Target.color = BaseColor;
        }

        private void Update()
        {
            float t = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Speed);
            float scale = 1f + ScaleAmount * t;
            if (_rect != null)
                _rect.localScale = new Vector3(scale, scale, 1f);
            if (Target != null)
                Target.color = Color.Lerp(BaseColor, PulseColor, t);
        }
    }

    /// <summary>
    /// ParaWASD-styled presentation of the live together cards. The real UICharacters
    /// cards remain the source of truth (selection, variants, success rolls and outcomes
    /// run through them); this view simply mirrors each visible card's data into our own
    /// rounded card row and the controller forwards keyboard input to the underlying card.
    /// </summary>
    internal sealed class ParaTogetherCardsView
    {
        public struct CardData
        {
            public UITogetherCard Source;
            public string Title;
            public Color BackgroundColor;
            public Color TextColor;
            public bool HasSuccessChance;
            public string SuccessText;
            public bool HasTrait;
            public Sprite TraitIcon;
            public string TraitLabel;
            public bool HasItem;
            public Sprite ItemIcon;
            public bool HasVariant;
            public bool HasRelationship;
            public string RelationshipText;
            // Owning Para's short name; only populated when the conversation involves
            // more than one Para offering cards, so the player can tell them apart.
            public string OwnerName;
        }

        public struct InitiativeData
        {
            public bool HasInitiative;
            public string NpcName;
            public Sprite NpcThumbnail;
            public string RequestText;
            // True when the request is a goal/quest the Para is asking the player to fulfill.
            public bool IsQuest;
            // Relationship-status color the base game paints behind the Para's thumbnail.
            public Color BackgroundColor;
        }

        private const int SortingOrder = 32001;
        private static readonly Color SelectedBorderColor = new Color(0.27f, 0.78f, 0.82f, 1f);
        private static readonly Color SelectedBorderPulse = new Color(0.55f, 0.92f, 0.95f, 1f);
        private static readonly Color UnselectedBorderColor = new Color(0f, 0f, 0f, 0.28f);
        private static readonly Color FooterColor = new Color(0.10f, 0.12f, 0.14f, 0.55f);
        private static readonly Color FooterTextColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color VariantBadgeColor = new Color(0.16f, 0.18f, 0.22f, 0.85f);
        // Banner + owner-label palette: matches the dialog's cream/slate/teal Paralives look.
        private static readonly Color BannerColor = new Color(0.98f, 0.97f, 0.93f, 0.98f);
        private static readonly Color BannerFrameColor = new Color(0.80f, 0.84f, 0.86f, 1f);
        private static readonly Color BannerNameColor = new Color(0.16f, 0.19f, 0.24f, 1f);
        private static readonly Color BannerTextColor = new Color(0.32f, 0.36f, 0.42f, 1f);
        private static readonly Color QuestAccentColor = new Color(0.95f, 0.70f, 0.25f, 1f);
        private static readonly Color OwnerLabelColor = new Color(0.32f, 0.36f, 0.42f, 0.9f);

        private readonly GameObject _root;
        private readonly RectTransform _row;
        private readonly Font _font;
        private readonly Sprite _rounded;
        private readonly List<CardView> _cards = new List<CardView>();

        private GameObject _bannerRoot;
        private Image _bannerThumb;
        private Image _bannerThumbBackground;
        private Image _bannerAccent;
        private Text _bannerName;
        private Text _bannerText;

        private sealed class CardView
        {
            public GameObject Root;
            public RectTransform Rect;
            public Image Border;
            public Image Background;
            public Text OwnerLabel;
            public Text Title;
            public Image Icon;
            public Text TraitLabel;
            public GameObject FooterRoot;
            public Text Footer;
            public GameObject VariantBadge;
            public UIPulse Pulse;
        }

        public ParaTogetherCardsView()
        {
            _root = new GameObject("ParaWASDTogetherCards");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            _font = ParaFonts.GameFont;
            _rounded = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");

            BuildInitiativeBanner();

            _row = CreateRect("CardRow", _root.transform);
            _row.anchorMin = new Vector2(0.5f, 0f);
            _row.anchorMax = new Vector2(0.5f, 0f);
            _row.pivot = new Vector2(0.5f, 0f);
            _row.anchoredPosition = new Vector2(0f, 286f);
            var layout = _row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 18f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            var fitter = _row.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
                _root.SetActive(visible);
        }

        public void Sync(IList<CardData> data, int selectedIndex, InitiativeData initiative)
        {
            UpdateInitiative(initiative);
            EnsureCards(data.Count);
            for (int i = 0; i < _cards.Count; i++)
            {
                var view = _cards[i];
                if (i >= data.Count)
                {
                    view.Pulse.enabled = false;
                    view.Root.SetActive(false);
                    continue;
                }

                var d = data[i];
                view.Root.SetActive(true);
                view.Background.color = d.BackgroundColor;
                view.Title.text = d.Title ?? string.Empty;
                view.Title.color = d.TextColor;

                bool hasOwner = !string.IsNullOrEmpty(d.OwnerName);
                view.OwnerLabel.gameObject.SetActive(hasOwner);
                if (hasOwner)
                    view.OwnerLabel.text = d.OwnerName;

                Sprite icon = d.HasTrait ? d.TraitIcon : (d.HasItem ? d.ItemIcon : null);
                view.Icon.sprite = icon;
                view.Icon.enabled = icon != null;

                bool hasTraitLabel = !string.IsNullOrEmpty(d.TraitLabel);
                view.TraitLabel.gameObject.SetActive(hasTraitLabel);
                if (hasTraitLabel)
                {
                    view.TraitLabel.text = d.TraitLabel;
                    view.TraitLabel.color = d.TextColor;
                }

                string footer = d.HasSuccessChance ? d.SuccessText : (d.HasRelationship ? d.RelationshipText : string.Empty);
                bool hasFooter = !string.IsNullOrEmpty(footer);
                view.FooterRoot.SetActive(hasFooter);
                if (hasFooter)
                    view.Footer.text = footer;

                view.VariantBadge.SetActive(d.HasVariant);

                bool selected = i == selectedIndex;
                view.Pulse.enabled = selected;
                if (!selected)
                {
                    view.Border.color = UnselectedBorderColor;
                    view.Root.transform.localScale = Vector3.one;
                }
            }
        }

        private void EnsureCards(int count)
        {
            while (_cards.Count < count)
                _cards.Add(CreateCard(_cards.Count));
        }

        // The banner surfaces what a Para is asking the player (an NPC initiative). When the
        // request is a goal/quest it gets a gold accent strip; otherwise it reads as a normal
        // conversation prompt. Sits above the card row, in the dialog's cream/slate style.
        private void BuildInitiativeBanner()
        {
            var banner = CreateRect("InitiativeBanner", _root.transform);
            banner.anchorMin = new Vector2(0.5f, 0f);
            banner.anchorMax = new Vector2(0.5f, 0f);
            banner.pivot = new Vector2(0.5f, 0f);
            banner.anchoredPosition = new Vector2(0f, 590f);
            banner.sizeDelta = new Vector2(620f, 124f);
            var bannerImage = banner.gameObject.AddComponent<Image>();
            bannerImage.sprite = _rounded;
            bannerImage.type = Image.Type.Sliced;
            bannerImage.color = BannerColor;
            _bannerRoot = banner.gameObject;

            var accent = CreateRect("BannerAccent", banner);
            accent.anchorMin = new Vector2(0f, 0f);
            accent.anchorMax = new Vector2(0f, 1f);
            accent.pivot = new Vector2(0f, 0.5f);
            accent.anchoredPosition = new Vector2(8f, 0f);
            accent.sizeDelta = new Vector2(5f, -16f);
            _bannerAccent = accent.gameObject.AddComponent<Image>();
            _bannerAccent.sprite = _rounded;
            _bannerAccent.type = Image.Type.Sliced;
            _bannerAccent.color = SelectedBorderColor;

            var frame = CreateRect("BannerThumbFrame", banner);
            frame.anchorMin = new Vector2(0f, 0.5f);
            frame.anchorMax = new Vector2(0f, 0.5f);
            frame.pivot = new Vector2(0f, 0.5f);
            frame.anchoredPosition = new Vector2(24f, 0f);
            frame.sizeDelta = new Vector2(72f, 72f);
            var frameImage = frame.gameObject.AddComponent<Image>();
            frameImage.sprite = _rounded;
            frameImage.type = Image.Type.Sliced;
            frameImage.color = BannerFrameColor;
            _bannerThumbBackground = frameImage;

            var thumb = CreateRect("BannerThumb", frame);
            thumb.anchorMin = Vector2.zero;
            thumb.anchorMax = Vector2.one;
            thumb.offsetMin = new Vector2(4f, 4f);
            thumb.offsetMax = new Vector2(-4f, -4f);
            _bannerThumb = thumb.gameObject.AddComponent<Image>();
            _bannerThumb.preserveAspect = true;
            _bannerThumb.color = Color.white;

            _bannerName = CreateText("BannerName", banner, 20, FontStyle.Bold, TextAnchor.LowerLeft);
            _bannerName.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            _bannerName.rectTransform.anchorMax = new Vector2(1f, 1f);
            _bannerName.rectTransform.pivot = new Vector2(0f, 1f);
            _bannerName.rectTransform.offsetMin = new Vector2(112f, 0f);
            _bannerName.rectTransform.offsetMax = new Vector2(-16f, -12f);
            _bannerName.color = BannerNameColor;

            _bannerText = CreateText("BannerText", banner, 19, FontStyle.Bold, TextAnchor.UpperLeft);
            _bannerText.rectTransform.anchorMin = new Vector2(0f, 0f);
            _bannerText.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            _bannerText.rectTransform.pivot = new Vector2(0f, 1f);
            _bannerText.rectTransform.offsetMin = new Vector2(112f, 12f);
            _bannerText.rectTransform.offsetMax = new Vector2(-16f, 0f);
            _bannerText.color = BannerTextColor;
            // Quest/goal descriptions vary a lot in length; shrink to fit rather than truncate.
            _bannerText.resizeTextForBestFit = true;
            _bannerText.resizeTextMaxSize = 19;
            _bannerText.resizeTextMinSize = 13;

            _bannerRoot.SetActive(false);
        }

        private void UpdateInitiative(InitiativeData initiative)
        {
            if (_bannerRoot == null)
                return;
            if (!initiative.HasInitiative)
            {
                _bannerRoot.SetActive(false);
                return;
            }

            _bannerRoot.SetActive(true);
            _bannerThumb.sprite = initiative.NpcThumbnail;
            _bannerThumb.enabled = initiative.NpcThumbnail != null;
            if (_bannerThumbBackground != null)
                _bannerThumbBackground.color = initiative.BackgroundColor;
            _bannerName.text = initiative.IsQuest
                ? (initiative.NpcName + " is asking you...")
                : initiative.NpcName;
            _bannerText.text = initiative.RequestText ?? string.Empty;
            _bannerAccent.color = initiative.IsQuest ? QuestAccentColor : SelectedBorderColor;
        }

        private CardView CreateCard(int index)
        {
            var border = CreateRect("Card" + index, _row);
            var borderImage = border.gameObject.AddComponent<Image>();
            borderImage.sprite = _rounded;
            borderImage.type = Image.Type.Sliced;
            borderImage.color = UnselectedBorderColor;
            var le = border.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 210f;
            le.preferredWidth = 210f;
            le.minHeight = 288f;
            le.preferredHeight = 288f;

            var bg = CreateRect("Bg", border);
            bg.anchorMin = Vector2.zero;
            bg.anchorMax = Vector2.one;
            bg.offsetMin = new Vector2(5f, 5f);
            bg.offsetMax = new Vector2(-5f, -5f);
            var bgImage = bg.gameObject.AddComponent<Image>();
            bgImage.sprite = _rounded;
            bgImage.type = Image.Type.Sliced;
            bgImage.color = Color.white;

            // Owner label: shows which Para offers this card (only filled in multi-Para chats).
            var ownerLabel = CreateText("Owner", bg, 16, FontStyle.Bold, TextAnchor.UpperCenter);
            ownerLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
            ownerLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
            ownerLabel.rectTransform.pivot = new Vector2(0.5f, 1f);
            ownerLabel.rectTransform.anchoredPosition = new Vector2(0f, -6f);
            ownerLabel.rectTransform.sizeDelta = new Vector2(-16f, 20f);
            ownerLabel.color = OwnerLabelColor;
            ownerLabel.gameObject.SetActive(false);

            var title = CreateText("Title", bg, 22, FontStyle.Bold, TextAnchor.UpperCenter);
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);
            title.rectTransform.anchoredPosition = new Vector2(0f, -30f);
            // Height stops short of the centered icon (icon top ≈ 92px from the card top) so
            // multi-line titles can't wrap underneath it. Best-fit shrinks long titles to fit
            // this band instead of overflowing into the icon.
            title.rectTransform.sizeDelta = new Vector2(-22f, 56f);
            title.resizeTextForBestFit = true;
            title.resizeTextMaxSize = 22;
            title.resizeTextMinSize = 13;

            var iconRect = CreateRect("Icon", bg);
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(0f, 10f);
            iconRect.sizeDelta = new Vector2(74f, 74f);
            var iconImage = iconRect.gameObject.AddComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.color = Color.white;

            var traitLabel = CreateText("Trait", bg, 18, FontStyle.Bold, TextAnchor.MiddleCenter);
            traitLabel.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            traitLabel.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            traitLabel.rectTransform.pivot = new Vector2(0.5f, 1f);
            traitLabel.rectTransform.anchoredPosition = new Vector2(0f, -30f);
            traitLabel.rectTransform.sizeDelta = new Vector2(-14f, 36f);

            var footer = CreateRect("Footer", bg);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.anchoredPosition = new Vector2(0f, 10f);
            footer.sizeDelta = new Vector2(-16f, 30f);
            var footerImage = footer.gameObject.AddComponent<Image>();
            footerImage.sprite = _rounded;
            footerImage.type = Image.Type.Sliced;
            footerImage.color = FooterColor;
            var footerText = CreateText("FooterText", footer, 17, FontStyle.Bold, TextAnchor.MiddleCenter);
            footerText.rectTransform.anchorMin = Vector2.zero;
            footerText.rectTransform.anchorMax = Vector2.one;
            footerText.rectTransform.offsetMin = Vector2.zero;
            footerText.rectTransform.offsetMax = Vector2.zero;
            footerText.color = FooterTextColor;

            var variantBadge = CreateRect("VariantBadge", bg);
            variantBadge.anchorMin = new Vector2(1f, 1f);
            variantBadge.anchorMax = new Vector2(1f, 1f);
            variantBadge.pivot = new Vector2(1f, 1f);
            variantBadge.anchoredPosition = new Vector2(-8f, -8f);
            variantBadge.sizeDelta = new Vector2(30f, 24f);
            var variantImage = variantBadge.gameObject.AddComponent<Image>();
            variantImage.sprite = _rounded;
            variantImage.type = Image.Type.Sliced;
            variantImage.color = VariantBadgeColor;
            var variantText = CreateText("VariantText", variantBadge, 15, FontStyle.Bold, TextAnchor.MiddleCenter);
            variantText.rectTransform.anchorMin = Vector2.zero;
            variantText.rectTransform.anchorMax = Vector2.one;
            variantText.rectTransform.offsetMin = Vector2.zero;
            variantText.rectTransform.offsetMax = Vector2.zero;
            variantText.color = Color.white;
            variantText.text = "R";

            var pulse = border.gameObject.AddComponent<UIPulse>();
            pulse.Target = borderImage;
            pulse.BaseColor = SelectedBorderColor;
            pulse.PulseColor = SelectedBorderPulse;
            pulse.ScaleAmount = 0.04f;
            pulse.Speed = 4.5f;
            pulse.enabled = false;

            return new CardView
            {
                Root = border.gameObject,
                Rect = border,
                Border = borderImage,
                Background = bgImage,
                OwnerLabel = ownerLabel,
                Title = title,
                Icon = iconImage,
                TraitLabel = traitLabel,
                FooterRoot = footer.gameObject,
                Footer = footerText,
                VariantBadge = variantBadge.gameObject,
                Pulse = pulse
            };
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        private Text CreateText(string name, Transform parent, int size, FontStyle style, TextAnchor alignment)
        {
            var rect = CreateRect(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }
    }

    /// <summary>
    /// Resolves the base-game UI typeface as a legacy <see cref="Font"/> so our runtime
    /// UnityEngine.UI text matches Paralives' own look. The game uses TMP font assets
    /// (FontReferences); we pull the TMP asset's underlying source TTF. Falls back to the
    /// built-in Arial if the source font was stripped from the shipped TMP asset.
    /// </summary>
    internal static class ParaFonts
    {
        private static Font _cached;

        public static Font GameFont
        {
            get
            {
                if (_cached != null)
                    return _cached;
                try
                {
                    var refs = FontReferences.Instance;
                    if (refs != null)
                    {
                        var tmp = refs.GetFont(FontTypes.Default);
                        if (tmp != null && tmp.sourceFontFile != null)
                        {
                            _cached = tmp.sourceFontFile;
                            return _cached;
                        }
                    }
                }
                catch { }
                _cached = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _cached;
            }
        }
    }

    internal static class ParaThumbnail
    {
        // The relationship-status background color the base game paints behind every
        // character thumbnail. Falls back to the supplied color if it can't be resolved.
        public static Color ResolveColor(AssetCharacter character, Color fallback)
        {
            if (character == null)
                return fallback;
            try
            {
                var mgr = CharacterManager.Instance;
                if (mgr != null)
                    return mgr.GetColorToDisplayOnCharacterThumbnail(character);
            }
            catch { }
            return fallback;
        }
    }
}
