using System;

namespace AtsBackgroundBuilder.Core
{
    internal static class PromptLifecycleService
    {
        internal static T ExecuteWithPromptRefresh<T>(Func<T> promptAction, Action refreshAction)
        {
            if (promptAction == null)
            {
                throw new ArgumentNullException(nameof(promptAction));
            }

            try
            {
                return promptAction();
            }
            finally
            {
                refreshAction?.Invoke();
            }
        }
    }
}
