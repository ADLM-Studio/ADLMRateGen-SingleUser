using ADLMRateGen.ADLM.Auth;
using ADLMRateGen.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ADLMRateGen.Services
{
    /// <summary>
    /// Cross-cutting commands invoked from the breakdown popup footer.
    /// Centralises the Save / Reset This Rate / Cancel behaviour so each of the
    /// eight *ItemDetailControl code-behinds can stay tiny.
    /// </summary>
    public static class RateEditCommands
    {
        /// <summary>
        /// Persist all in-memory overrides to disk and push to the cloud (QUIV/HERON sync).
        /// Returns true on a successful local save (caller can then show a "Saved" toast).
        /// Cloud push failures degrade silently — local save survives so edits aren't lost.
        /// </summary>
        public static async Task<bool> SaveAsync()
        {
            try
            {
                UserRateEditStore.Current.SaveToDisk();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RateEditCommands.SaveAsync] Local save failed: {ex.Message}");
                ADLMRateGen.Helpers.AppMessage.Show(
                    "Your changes could not be saved locally. Please try again.",
                    "Save Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            // Best-effort cloud push so other devices and the QUIV / HERON plugins see the edits.
            try
            {
                var mainVm = TryGetMainViewModel();
                if (mainVm != null && AuthProvider.Instance.Client.HasSession)
                {
                    await UserRatesCloudSync.Instance
                        .PushSnapshotAsync(mainVm, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RateEditCommands.SaveAsync] Cloud push failed: {ex.Message}");
                // Local save already succeeded — surface a soft warning, don't block.
                ADLMRateGen.Helpers.AppMessage.Show(
                    "Your edits were saved on this device, but couldn't sync to the cloud right now. " +
                    "They'll sync automatically the next time you're connected.",
                    "Cloud Sync Pending",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }

        /// <summary>
        /// Reset all Quantity overrides for the given item back to shipped defaults,
        /// then persist + push so the reset propagates to QUIV/HERON.
        /// </summary>
        public static async Task ResetItemAsync(string sectionKey, int itemNo)
        {
            var removed = UserRateEditStore.Current.ClearItem(sectionKey, itemNo);
            if (removed == 0) return;

            await SaveAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Discard any in-memory edits made during the current popup session by
        /// reloading the on-disk state. The popup then recomputes with the saved values.
        /// </summary>
        public static void CancelUnsavedEdits()
        {
            UserRateEditStore.Current.ReloadFromDisk();
        }

        /// <summary>
        /// App-wide reset: wipe every override across every section, save, push.
        /// Caller is responsible for the confirm dialog before invoking this.
        /// </summary>
        public static async Task ResetAllAsync()
        {
            UserRateEditStore.Current.ClearAll();
            await SaveAsync().ConfigureAwait(false);
        }

        private static MainViewModel? TryGetMainViewModel()
        {
            // The MainWindow's DataContext is the MainViewModel — works from any UI thread context.
            try
            {
                if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
                {
                    return vm;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RateEditCommands] TryGetMainViewModel failed: {ex.Message}");
            }
            return null;
        }
    }
}
