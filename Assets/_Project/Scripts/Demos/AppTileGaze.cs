using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Demos
{
    // Shows an app's tagline under the library's grid while the gaze is on its tile (or on a rail item).
    public class AppTileGaze : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] TMP_Text m_Target;
        [SerializeField] string m_Tagline;
        [SerializeField] string m_Prompt;

        // Changed while it shows (a switch that was just flipped), it shows the new line at once.
        public string Tagline
        {
            get => m_Tagline;
            set
            {
                var showing = m_Target != null && m_Target.text == m_Tagline;
                m_Tagline = value;
                if (showing)
                    m_Target.text = value;
            }
        }

        // What shows once the gaze leaves.
        public string Prompt
        {
            get => m_Prompt;
            set => m_Prompt = value;
        }

        public void Initialize(TMP_Text target, string tagline, string prompt)
        {
            m_Target = target;
            m_Tagline = tagline;
            m_Prompt = prompt;
        }

        public void OnPointerEnter(PointerEventData eventData) => m_Target.text = m_Tagline;

        public void OnPointerExit(PointerEventData eventData)
        {
            if (m_Target.text == m_Tagline)
                m_Target.text = m_Prompt;
        }
    }
}
