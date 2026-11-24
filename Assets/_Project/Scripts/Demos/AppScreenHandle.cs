using CardboardXR.Hands;
using CardboardXR.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Demos
{
    // The handle under an app's screen in XR (AppScreen): pinch it, or hold a tap on it, and the screen
    // follows the pinching hand (or, from a tap, the gaze) around the head until let go. PinchRouter asks
    // it first when the gaze is on it, as it does the screen itself.
    public class AppScreenHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IGazeDrag,
        IPinchHandler
    {
        AppScreen m_Screen;
        bool m_Pinching;
        bool m_Pressed;

        public void Initialize(AppScreen screen) => m_Screen = screen;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (m_Screen == null || m_Pinching)
                return;
            m_Pressed = true;
            m_Screen.BeginMove(byHand: false);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!m_Pressed)
                return;
            m_Pressed = false;
            m_Screen.EndMove();
        }

        // Here so the reticle shows the ring of something that can be pressed.
        public void OnPointerClick(PointerEventData eventData) { }

        // The screen follows on its own (AppScreen.LateUpdate); this keeps the press while the gaze moves.
        public void OnGazeDrag(Ray ray) { }

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source)
        {
            if (gazeTarget != gameObject || m_Screen == null || !AppScreen.IsOpen)
                return false;
            m_Pinching = true;
            m_Screen.SetPinchHeld(true);
            m_Screen.BeginMove(byHand: source == PinchSource.Camera);
            return true;
        }

        public void EndPinch()
        {
            if (!m_Pinching)
                return;
            m_Pinching = false;
            m_Screen.SetPinchHeld(false);
            m_Screen.EndMove();
        }

        void OnDisable()
        {
            EndPinch();
            if (m_Pressed)
            {
                m_Pressed = false;
                m_Screen?.EndMove();
            }
        }
    }
}
