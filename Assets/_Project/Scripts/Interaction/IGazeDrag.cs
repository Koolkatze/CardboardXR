using UnityEngine;

namespace CardboardXR.Interaction
{
    // Something that, pressed with the gaze (a screen tap held down), follows the gaze until it's let go:
    // a phone app's screen in XR, which drags its finger as the head turns, to scroll. GazePointer gives
    // it the pointing ray each frame of the press, and a press held on it isn't a hold (OnHold), which
    // would recenter the view in the middle of a drag.
    public interface IGazeDrag
    {
        void OnGazeDrag(Ray ray);
    }
}
