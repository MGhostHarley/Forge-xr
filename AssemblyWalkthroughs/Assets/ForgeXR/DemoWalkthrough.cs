using UnityEngine;

namespace ForgeXR
{
    public sealed class DemoWalkthrough : MonoBehaviour
    {
        private static readonly string[] WalkthroughNames = { "Driver", "Wheel" };

        [SerializeField] private DriverWalkthrough driver;
        [SerializeField] private WheelWalkthrough wheel;

        private bool wheelSelected;

        public DesktopWalkthrough ActiveWalkthrough => wheelSelected ? (DesktopWalkthrough)wheel : driver;

        public void SelectDriver() => wheelSelected = false;
        public void SelectWheel() => wheelSelected = true;
        public void Advance() => ActiveWalkthrough.Advance();
        public void ResetWalkthrough() => ActiveWalkthrough.ResetWalkthrough();

        private void OnGUI()
        {
            bool selectWheel = wheelSelected;
            ActiveWalkthrough.DrawPanel(() =>
            {
                int selectedIndex = GUILayout.Toolbar(wheelSelected ? 1 : 0, WalkthroughNames);
                selectWheel = selectedIndex == 1;
            });
            wheelSelected = selectWheel;
        }
    }
}
