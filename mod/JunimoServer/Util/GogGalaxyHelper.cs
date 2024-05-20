using System;
using Galaxy.Api;

namespace JunimoServer.Util
{
    // Copied from  StardewValley.SDKs.GogGalaxy.Listeners
    public class GalaxyAuthListener : IAuthListener
    {
        //
        // Summary:
        //     The callback to invoke when Galaxy user authentication succeeds.
        private readonly Action OnSuccess;

        //
        // Summary:
        //     The callback to invoke when Galaxy user authentication fails.
        private readonly Action<FailureReason> OnFailure;

        //
        // Summary:
        //     The callback to invoke when Galaxy loses user authentication.
        private readonly Action OnLost;

        //
        // Summary:
        //     Constructs an instance of the listener and registers it with the Galaxy SDK.
        //
        // Parameters:
        //   success:
        //     The callback to invoke when Galaxy user authentication succeeds.
        //
        //   failure:
        //     The callback to invoke when Galaxy user authentication fails.
        //
        //   lost:
        //     The callback to invoke when Galaxy loses user authentication.
        public GalaxyAuthListener(Action success, Action<FailureReason> failure, Action lost)
        {
            OnSuccess = success;
            OnFailure = failure;
            OnLost = lost;
            GalaxyInstance.ListenerRegistrar().Register(GalaxyTypeAwareListenerAuth.GetListenerType(), this);
        }

        //
        // Summary:
        //     Handles user authentication success, and invokes StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener.OnSuccess.
        public override void OnAuthSuccess()
        {
            OnSuccess?.Invoke();
        }

        //
        // Summary:
        //     Handles user authentication failure, and invokes StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener.OnFailure.
        public override void OnAuthFailure(FailureReason reason)
        {
            OnFailure?.Invoke(reason);
        }

        //
        // Summary:
        //     Handles loosing user authentication, and invokes StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener.OnLost.
        public override void OnAuthLost()
        {
            OnLost?.Invoke();
        }

        //
        // Summary:
        //     Unregisters the listener from the Galaxy SDK.
        public override void Dispose()
        {
            GalaxyInstance.ListenerRegistrar().Unregister(GalaxyTypeAwareListenerAuth.GetListenerType(), this);
            base.Dispose();
        }
    }

    class GalaxyOperationalStateChangeListener : IOperationalStateChangeListener
    {
        //
        // Summary:
        //     The callback to invoke when Galaxy's operational state changes.
        private readonly Action<uint> Callback;

        //
        // Summary:
        //     Constructs an instance of the listener and registers it with the Galaxy SDK.
        //
        // Parameters:
        //   callback:
        //     The callback to invoke when Galaxy's operational state changes.
        public GalaxyOperationalStateChangeListener(Action<uint> callback)
        {
            Callback = callback;
            GalaxyInstance.ListenerRegistrar().Register(GalaxyTypeAwareListenerOperationalStateChange.GetListenerType(), this);
        }

        //
        // Summary:
        //     Handles operational state changes, and passes the information to StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener.Callback.
        //
        // Parameters:
        //   operationalState:
        //     A bit-field representing the operational state change.
        public override void OnOperationalStateChanged(uint operationalState)
        {
            Callback?.Invoke(operationalState);
        }

        //
        // Summary:
        //     Unregisters the listener from the Galaxy SDK.
        public override void Dispose()
        {
            GalaxyInstance.ListenerRegistrar().Unregister(GalaxyTypeAwareListenerOperationalStateChange.GetListenerType(), this);
            base.Dispose();
        }
    }
}