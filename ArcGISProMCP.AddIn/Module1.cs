using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISProMCP.AddIn.Services;

namespace ArcGISProMCP.AddIn
{
    internal class Module1 : Module
    {
        internal const string ModuleId = "ArcGISProMCP_AddIn_Module";
        private static Module1 _this = null;
        private readonly ArcGisCommandService _commands = new();
        private BridgeService _bridge;
        internal BridgeService Bridge => _bridge ??= new BridgeService(handler: _commands);

        /// <summary>
        /// Retrieve the singleton instance to this module here
        /// </summary>
        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule(ModuleId);

        #region Overrides
        // Loading the module does not start the listener; the user starts it explicitly.
        protected override bool Initialize() => base.Initialize();

        protected override void Uninitialize()
        {
            // Cancel immediately, then finish cleanup asynchronously. Never block the UI on the MCT.
            _commands.Dispose();
            if (_bridge != null) _ = _bridge.StopAsync();
            _this = null;
            base.Uninitialize();
        }

        /// <summary>
        /// Called by Framework when ArcGIS Pro is closing
        /// </summary>
        /// <returns>False to prevent Pro from closing, otherwise True</returns>
        protected override bool CanUnload()
        {
            return true;
        }

        #endregion Overrides

    }
}
