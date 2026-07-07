using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianModifierStateMachine))]
    public class GaussianModifierStateMachineEditor : UnityEditor.Editor
    {
        private SerializedProperty _statesProp;
        private SerializedProperty _defaultStateProp;
        private SerializedProperty _onStateChangedProp;

        private void OnEnable()
        {
            _statesProp = serializedObject.FindProperty("_states");
            _defaultStateProp = serializedObject.FindProperty("_defaultState");
            _onStateChangedProp = serializedObject.FindProperty("_onStateChanged");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var sm = (GaussianModifierStateMachine)target;

            // Default state
            EditorGUILayout.PropertyField(_defaultStateProp);
            EditorGUILayout.Space();

            // States list with per-state Capture buttons
            EditorGUILayout.LabelField("States", EditorStyles.boldLabel);

            for (int i = 0; i < _statesProp.arraySize; i++)
            {
                var stateProp = _statesProp.GetArrayElementAtIndex(i);
                var nameProp = stateProp.FindPropertyRelative("name");
                string stateName = string.IsNullOrEmpty(nameProp.stringValue) ? $"State {i}" : nameProp.stringValue;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Header row: foldout + Capture button
                EditorGUILayout.BeginHorizontal();
                stateProp.isExpanded = EditorGUILayout.Foldout(stateProp.isExpanded, stateName, true);
                if (GUILayout.Button("Capture", GUILayout.Width(60)))
                {
                    Undo.RecordObject(target, "Capture State Params");
                    sm.CaptureStateParams(i);
                    EditorUtility.SetDirty(target);
                    serializedObject.Update();
                }
                if (GUILayout.Button("Apply", GUILayout.Width(50)))
                {
                    foreach (var mod in sm.GetComponents<GaussianAnimModifier>())
                        Undo.RecordObject(mod, "Apply State Params");
                    sm.ApplyStateParams(i);
                }
                using (new EditorGUI.DisabledScope(!Application.isPlaying || (sm.CurrentState == nameProp.stringValue && !sm.IsTransitioning)))
                {
                    if (GUILayout.Button("Go", GUILayout.Width(30)))
                    {
                        sm.SetState(nameProp.stringValue);
                    }
                }
                if (GUILayout.Button("-", GUILayout.Width(22)))
                {
                    _statesProp.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                if (stateProp.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(nameProp);
                    EditorGUILayout.PropertyField(stateProp.FindPropertyRelative("modifierParams"), true);
                    EditorGUILayout.PropertyField(stateProp.FindPropertyRelative("transitions"), true);
                    EditorGUILayout.PropertyField(stateProp.FindPropertyRelative("onEnter"));
                    EditorGUILayout.PropertyField(stateProp.FindPropertyRelative("onExit"));
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }

            // Add / Auto-Populate buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add State"))
            {
                _statesProp.InsertArrayElementAtIndex(_statesProp.arraySize);
                var newState = _statesProp.GetArrayElementAtIndex(_statesProp.arraySize - 1);
                newState.FindPropertyRelative("name").stringValue = "";
                newState.FindPropertyRelative("modifierParams").ClearArray();
                newState.FindPropertyRelative("transitions").ClearArray();
                // InsertArrayElementAtIndex deep-copies the previous state —
                // clear the copied onEnter/onExit persistent listeners too,
                // or the new state silently fires the old state's callbacks.
                newState.FindPropertyRelative("onEnter.m_PersistentCalls.m_Calls").ClearArray();
                newState.FindPropertyRelative("onExit.m_PersistentCalls.m_Calls").ClearArray();
            }
            if (GUILayout.Button("Auto-Populate Empty States"))
            {
                Undo.RecordObject(target, "Auto-Populate States");
                sm.AutoPopulateModifiers();
                EditorUtility.SetDirty(target);
                serializedObject.Update();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Events
            EditorGUILayout.PropertyField(_onStateChangedProp);

            serializedObject.ApplyModifiedProperties();

            // Play Mode runtime info
            if (Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Current State", sm.CurrentState ?? "(none)");
                EditorGUILayout.LabelField("Transitioning", sm.IsTransitioning.ToString());

                if (sm.States != null && sm.States.Count > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Quick Switch", EditorStyles.boldLabel);
                    foreach (var state in sm.States)
                    {
                        if (string.IsNullOrEmpty(state.name)) continue;
                        bool isCurrent = sm.CurrentState == state.name;
                        using (new EditorGUI.DisabledScope(isCurrent))
                        {
                            if (GUILayout.Button(isCurrent ? $"● {state.name}" : state.name))
                            {
                                sm.SetState(state.name);
                            }
                        }
                    }
                }

                Repaint();
            }
        }
    }
}
