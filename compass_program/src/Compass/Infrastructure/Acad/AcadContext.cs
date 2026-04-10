using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Compass.Infrastructure.Acad;

public static class AcadContext
{
    public static T Run<T>(Database database, bool write, Func<Transaction, T> action)
    {
        if (database == null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var document = Application.DocumentManager.MdiActiveDocument;
        using (document?.LockDocument())
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            try
            {
                var result = action(transaction);
                if (write)
                {
                    transaction.Commit();
                }
                else
                {
                    transaction.Abort();
                }

                return result;
            }
            catch
            {
                transaction.Abort();
                throw;
            }
        }
    }

    public static void Run(Database database, bool write, Action<Transaction> action)
    {
        Run(database, write, transaction =>
        {
            action(transaction);
            return true;
        });
    }
}
