using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TxtAIEditor.Core.Models
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _isNotificationSuspended;

        public void AddRange(IEnumerable<T> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            _isNotificationSuspended = true;
            try
            {
                foreach (var item in items)
                {
                    Add(item);
                }
            }
            finally
            {
                _isNotificationSuspended = false;
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_isNotificationSuspended)
            {
                base.OnCollectionChanged(e);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_isNotificationSuspended)
            {
                base.OnPropertyChanged(e);
            }
        }
    }
}
