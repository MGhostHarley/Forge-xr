using UnityEngine;

namespace ForgeXR
{
    public abstract class DesktopWalkthrough : MonoBehaviour
    {
        protected abstract string Title { get; }
        protected abstract string CompletionMessage { get; }
        protected abstract int StepCount { get; }
        protected abstract string Instruction { get; }
        protected abstract string PartCue { get; }
        public abstract Texture2D CurrentStill { get; }

        public int StepIndex { get; private set; }
        public bool IsComplete => StepIndex == StepCount;
        public string CurrentInstruction => IsComplete ? CompletionMessage : Instruction;
        public string CurrentPart => IsComplete ? string.Empty : PartCue;

        public void Advance()
        {
            if (!IsComplete)
                StepIndex++;
        }

        public void ResetWalkthrough()
        {
            StepIndex = 0;
        }

        protected void OnGUI()
        {
            Event input = Event.current;
            // KeyUp advances once per press, including when Space is held down.
            if (input.type == EventType.KeyUp && input.keyCode == KeyCode.Space && !IsComplete)
            {
                Advance();
                input.Use();
            }

            float width = Mathf.Min(Screen.width - 24, 560);
            float height = Screen.height - 24;
            GUILayout.BeginArea(new Rect((Screen.width - width) / 2, 12, width, height), GUI.skin.box);
            GUILayout.Label(Title, GUI.skin.label);
            GUILayout.Label(IsComplete ? "Complete" : $"Step {StepIndex + 1} of {StepCount}");
            GUILayout.Space(8);
            GUILayout.Label(CurrentInstruction, new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 18 });

            if (!IsComplete && !string.IsNullOrEmpty(CurrentPart))
                GUILayout.Label($"Pick up: {CurrentPart}");

            Rect stillArea = GUILayoutUtility.GetRect(0, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (CurrentStill != null)
                GUI.DrawTexture(stillArea, CurrentStill, ScaleMode.ScaleToFit);

            if (IsComplete)
            {
                if (GUILayout.Button("Reset", GUILayout.Height(34)))
                    ResetWalkthrough();
            }
            else if (GUILayout.Button(StepIndex == StepCount - 1 ? "Done (Space)" : "Next (Space)", GUILayout.Height(34)))
            {
                Advance();
            }

            GUILayout.EndArea();
        }
    }
}
