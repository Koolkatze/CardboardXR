using CardboardXR.Hands;
using CardboardXR.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Demos
{
    // The app's display on its screen in XR (AppScreen), touched where the gaze is:
    //   - a tap of the phone's screen (the viewer's button) touches down when pressed, follows the gaze
    //     while held (IGazeDrag), and lifts when let go;
    //   - a pinch does the same, from when the fingers close until they open (PinchRouter asks it first,
    //     as what the gaze is on). With the settings panel open a pinch only clicks: down and up at once.
    // So a quick tap or pinch taps the app, and holding while looking along drags, which scrolls. The
    // gaze is the pointing ray: the head's, or the hand's when one points (GazePointer.CurrentRay).
    public class AppScreenSurface : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IGazeDrag,
        IPinchHandler
    {
        AppScreen m_Screen;
        bool m_Pinching;

        public void Initialize(AppScreen screen) => m_Screen = screen;

        public void OnPointerDown(PointerEventData eventData) => Touch(AppScreen.k_Down);

        public void OnGazeDrag(Ray ray) => m_Screen.TouchAt(AppScreen.k_Move, ray);

        public void OnPointerUp(PointerEventData eventData) => Touch(AppScreen.k_Up);

        // Here so the reticle shows the ring of something that can be pressed; the touch is down and up.
        public void OnPointerClick(PointerEventData eventData) { }

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source)
        {
            if (gazeTarget != gameObject || m_Screen == null || !AppScreen.IsOpen)
                return false;
            m_Pinching = true;
            Touch(AppScreen.k_Down);
            return true;
        }

        public void EndPinch()
        {
            if (!m_Pinching)
                return;
            m_Pinching = false;
            Touch(AppScreen.k_Up);
        }

        void Update()
        {
            if (m_Pinching)
                Touch(AppScreen.k_Move);
        }

        void OnDisable() => EndPinch();

        void Touch(int action)
        {
            var core = CoreServices.Instance;
            if (m_Screen == null || core == null || core.Gaze == null)
                return;
            m_Screen.TouchAt(action, core.Gaze.CurrentRay);
        }
    }
}
