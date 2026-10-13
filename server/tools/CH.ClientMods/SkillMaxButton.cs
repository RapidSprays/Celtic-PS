using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CHPrivateMods
{
    /// <summary>
    /// Adds a "Max" button above the skill popup's Upgrade button. It sets the upgrade target as high as the game allows
    /// (the popup's own SetLevelToUpgradeTo clamps to the rank cap and free points) and confirms, so one tap spends as
    /// many points as the skill can take. The server clamps again. Everything is reflection on the popup (its members
    /// are internal), and any failure leaves the popup as it was.
    /// </summary>
    public static class SkillMaxButton
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Called at the end of CSkillPopup's constructor (patch P10).</summary>
        public static void Attach(object popup)
        {
            try
            {
                var type = popup.GetType();
                var upgrade = R.Field(type, "m_btnUpgrade")?.GetValue(popup) as Button;
                if (upgrade == null) return;
                var setLevel = R.Method(type, "SetLevelToUpgradeTo", 1);
                var confirm = R.Method(type, "ConfirmButtonPressed", 0);
                if (setLevel == null || confirm == null) return;

                var copy = UnityEngine.Object.Instantiate(upgrade.gameObject, upgrade.transform.parent, false);
                copy.name = "btnMaxSkill";
                var source = (RectTransform)upgrade.transform;
                var rect = (RectTransform)copy.transform;
                rect.anchoredPosition = source.anchoredPosition + new Vector2(0f, source.rect.height + 6f);
                foreach (var component in copy.GetComponentsInChildren<Component>(true))
                    if (component != null && component.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0)
                        UnityEngine.Object.Destroy(component); // a localisation component would put "Upgrade" back
                SetLabel(copy);

                var button = copy.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() =>
                {
                    try
                    {
                        setLevel.Invoke(popup, new object[] { int.MaxValue });
                        confirm.Invoke(popup, null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[CHPrivateMods] Max failed: " + e);
                    }
                });

                // Show the Max button exactly when Upgrade is shown (the popup hides Upgrade in some states).
                var mirror = upgrade.transform.parent.gameObject.AddComponent<MaxButtonMirror>();
                mirror.Source = upgrade;
                mirror.Copy = copy;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CHPrivateMods] Max button not added: " + e);
            }
        }

        internal static void SetLabel(GameObject button)
        {
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true)) label.text = "Max";
            foreach (var label in button.GetComponentsInChildren<Text>(true)) label.text = "Max";
        }
    }

    /// <summary>Keeps the Max button's visibility and enabled state in step with the Upgrade button.</summary>
    public sealed class MaxButtonMirror : MonoBehaviour
    {
        public Button Source;
        public GameObject Copy;

        private void LateUpdate()
        {
            if (Source == null || Copy == null) return;
            var show = Source.gameObject.activeSelf;
            if (Copy.activeSelf != show) Copy.SetActive(show);
            var button = Copy.GetComponent<Button>();
            if (button != null && button.interactable != Source.interactable) button.interactable = Source.interactable;
            SkillMaxButton.SetLabel(Copy);
        }
    }
}
