using System;
using System.Collections.Generic;
using Setting;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ParaWASD
{
    public class ParaWASDController : MonoBehaviour
    {
        public static ParaWASDController ActiveInstance { get; private set; }

        // State
        public bool IsActive { get; private set; }
        public bool IsCursorMode => _cursorMode;
        private float _pitch;
        private float _yaw;
        private Transform _headBone;
        private Camera _gameCamera;
        private Vector3 _savedCameraPosition;
        private Quaternion _savedCameraRotation;
        private float _savedCameraFOV;
        private float _savedNearClip;

        // Autonomy
        private bool _savedAutonomyForSelected;

        // Mouse mode
        private bool _cursorMode;
        public bool IsLookMode => IsActive && !_cursorMode;

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

        // Interaction menu keyboard nav
        private int _menuSelectedIndex = -1;
        private int _menuActiveDepth;
        private Dictionary<int, int> _menuSelectedIndexByDepth = new Dictionary<int, int>();
        private bool _menuWasVisible;
        private UIInteractionsListItem _lastHoveredItem;
        private int _suppressInteractionMenuInputFrames;

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

            if (!_cursorMode)
            {
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

            ForceLookCursorLock();
            _mouseDeltaThisFrame = Vector2.zero;
            _suppressMouseLookFrames = 2;

            var gameplay = Settings.Get<Gameplay>();
            _savedAutonomyForSelected = gameplay.EnableAutonomyForSelectedCharacters;
            gameplay.EnableAutonomyForSelectedCharacters = false;
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
            _snapCameraNextFrame = true;
        }

        public void Deactivate()
        {
            CleanupAllDoors();
            SetCharacterVisible(true);
            Settings.Get<Gameplay>().EnableAutonomyForSelectedCharacters = _savedAutonomyForSelected;

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

        private void LateUpdate()
        {
            if (!IsActive) return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Let the game close interaction menus before exiting ParaWASD.
                var uiCheck = UI.GetOrNull<UIInteractions>(0);
                if (uiCheck != null && uiCheck.IsVisible)
                    return;

                Deactivate();
                return;
            }

            if (Input.GetKeyDown(KeyCode.LeftAlt))
                _cursorMode = !_cursorMode;

            if (_headBone == null || _followedVisual == null)
            {
                if (!TryAcquireTarget())
                {
                    Deactivate();
                    return;
                }
            }

            if (!_cursorMode)
            {
                HandleMouseLook();
                if (Input.GetKeyDown(KeyCode.C))
                    TryCancelCurrentAction();
                if (Input.GetKeyDown(KeyCode.E))
                    TryOpenCenterInteractionMenu();
            }

            bool charBusy = IsCharacterPerformingAction();
            if (!_cursorMode && !charBusy)
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
            UpdateCameraPosition();
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

            if (TryNavMeshMove(characterAsset, desiredPos, 0.2f, areaMask)) return;
            if (TryNavMeshMove(characterAsset, desiredPos, 0.5f, areaMask)) return;

            // Fallback for reachable non-stair elevation changes.
            if (TryStartPathTo(characterAsset, desiredPos + moveDir * 1.5f)) return;

            var slideX = new Vector3(desiredPos.x, currentPos.y, currentPos.z);
            if (TryNavMeshMove(characterAsset, slideX, 0.2f, areaMask)) return;
            var slideZ = new Vector3(currentPos.x, currentPos.y, desiredPos.z);
            TryNavMeshMove(characterAsset, slideZ, 0.2f, areaMask);
        }

        private bool TryNavMeshMove(AssetCharacter characterAsset, Vector3 pos, float radius, int areaMask)
        {
            if (!NavMesh.SamplePosition(pos, out var hit, radius, areaMask))
                return false;
            if (Mathf.Abs(hit.position.y - characterAsset.Data.Position.y) > 0.3f)
                return false;
            characterAsset.Data.Position = hit.position;
            characterAsset.Data.Rotation = Quaternion.Euler(0, _yaw, 0);
            return true;
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

        private bool TryStartPathTo(AssetCharacter characterAsset, Vector3 targetPos)
        {
            int areaMask = GetFullAreaMask();
            if (!NavMesh.SamplePosition(targetPos, out var targetHit, 2.0f, areaMask)) return false;
            if (!NavMesh.SamplePosition(characterAsset.Data.Position, out var startHit, 1.0f, areaMask)) return false;

            NavMesh.CalculatePath(startHit.position, targetHit.position, areaMask, _navPath);
            if (_navPath.status == NavMeshPathStatus.PathInvalid) return false;

            _pathCorners = _navPath.corners;
            if (_pathCorners.Length < 2) return false;
            if (PathContainsStairLink(_pathCorners)) return false;

            float maxYDiff = 0f;
            for (int i = 1; i < _pathCorners.Length; i++)
            {
                float yDiff = Mathf.Abs(_pathCorners[i].y - _pathCorners[0].y);
                if (yDiff > maxYDiff) maxYDiff = yDiff;
            }
            if (maxYDiff < 0.3f) return false;

            _pathIndex = 1;
            _isTraversingPath = true;
            return true;
        }

        private bool PathContainsStairLink(Vector3[] corners)
        {
            if (corners == null || PathfindingManager.Instance == null)
                return false;

            for (int i = 1; i < corners.Length; i++)
            {
                var linkResult = PathfindingManager.Instance.NavMeshLinkList.FindLink(corners[i - 1], corners[i]);
                if (linkResult.PathLink != null && linkResult.PathLink.NavMeshLinkType == NavMeshLinkType.Stairs)
                    return true;
            }
            return false;
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
        private void HandleInteractionMenuKeyboard()
        {
            var uiInteractions = UI.GetOrNull<UIInteractions>(0);
            bool menuVisible = uiInteractions != null && uiInteractions.IsVisible;

            // Menu just opened
            if (menuVisible && !_menuWasVisible)
            {
                _cursorMode = true;
                _menuSelectedIndex = 0;
                _menuActiveDepth = 0;
                _menuSelectedIndexByDepth.Clear();
                _menuSelectedIndexByDepth[0] = 0;
                HighlightInteractionItem(uiInteractions, 0, 0);
            }
            // Menu just closed
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
            if (!hit.HasHit)
            {
                Debug.Log("[ParaWASD] Center interact found no valid target.");
                return;
            }

            if (OpenInteractionMenuForHit(player, hit))
            {
                _cursorMode = true;
                _isTraversingPath = false;
                _suppressInteractionMenuInputFrames = 1;
            }
        }

        private CenterInteractionHit RaycastCenterForInteraction()
        {
            CenterInteractionHit result = default;
            if (_gameCamera == null || UnityLayersManager.Instance == null)
                return result;

            Physics.SyncTransforms();
            var ray = _gameCamera.ScreenPointToRay(GetScreenCenterPosition(0));
            int layerMask = UnityLayersManager.Instance.RaycastLayerMask | (1 << LayerMask.NameToLayer("CharacterVisual"));
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
                result.RaycastObject = target.RaycastObject;
                return result;
            }

            return result;
        }

        private bool OpenInteractionMenuForHit(Player player, CenterInteractionHit hit)
        {
            var uiInteractions = UI.GetOrNull<UIInteractions>(player.PlayerIndex);
            if (uiInteractions == null)
                return false;

            var interactions = Settings.Get<Interactions>();
            if (interactions == null)
                return false;

            if (hit.ColliderType == ColliderType.Object)
            {
                var item = hit.RaycastObject != null ? hit.RaycastObject.GetComponent<ItemObjectRoot>() : null;
                if (item == null || !InteractionManager.Instance.ItemHasInteractions(item, player.PlayerIndex))
                    return false;

                var group = InteractionManager.Instance.GetItemInteractionGroup(item);
                uiInteractions.Show(group, hit.WorldPosition, item.InstanceID, 0UL, item, item.LotPlacedOnGUID);
                Debug.Log("[ParaWASD] Opened center interaction menu for item.");
                return true;
            }

            if (hit.ColliderType == ColliderType.Floor || hit.ColliderType == ColliderType.Terrain)
            {
                var group = interactions.GetInteractionGroupByGUID(interactions.FloorInteractions);
                ulong lotGUID = LotManager.Instance != null ? LotManager.Instance.GetLotFromPosition(hit.WorldPosition) : 0UL;
                uiInteractions.Show(group, hit.WorldPosition, -1, 0UL, null, lotGUID);
                Debug.Log("[ParaWASD] Opened center interaction menu for ground.");
                return true;
            }

            if (hit.ColliderType == ColliderType.Character)
            {
                var targetCharacter = CharacterManager.Instance.GetCharacterByGUID(hit.CharacterGUID);
                if (targetCharacter == null || targetCharacter.Data.IsDead)
                    return false;

                ulong groupGUID = InteractionManager.Instance.GetInteractionGroupGUIDFor(interactions, player.SelectedCharactersGUID, hit.CharacterGUID);
                var group = interactions.GetInteractionGroupByGUID(groupGUID);
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
                Debug.Log("[ParaWASD] Opened center interaction menu for character.");
                return true;
            }

            return false;
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

        private void SetCharacterVisible(bool visible)
        {
            if (_followedVisual == null) return;
            var renderers = _followedVisual.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
                r.enabled = visible;
        }
    }
}
