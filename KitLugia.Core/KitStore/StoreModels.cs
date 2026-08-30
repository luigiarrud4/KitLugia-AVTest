using System;
using System.ComponentModel;

namespace KitLugia.Core.KitStore
{
    /// <summary>
    /// Modelo de app unificado (winget / choco / msstore) — puro POCO no Core.
    /// GUI cria ViewModel (StoreAppView) que envolve este modelo + IconSource.
    /// Mantido leve para serialização e reuso em CLI / janela standalone.
    /// </summary>
    public class StoreApp : INotifyPropertyChanged
    {
        private string _name = "";
        private string _id = "";
        private string _publisher = "";
        private string _version = "";
        private string _availableVersion = "";
        private string _source = "winget";
        private string _category = "";
        private string _description = "";
        private double _rating;
        private int _ratingCount;
        private bool _isFree = true;

        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } }
        public string Id { get => _id; set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } } }
        public string Publisher { get => _publisher; set { if (_publisher != value) { _publisher = value; OnPropertyChanged(nameof(Publisher)); } } }
        public string Version
        {
            get => _version;
            set { if (_version != value) { _version = value; OnPropertyChanged(nameof(Version)); OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(HasUpdateFlag)); } }
        }
        public string AvailableVersion
        {
            get => _availableVersion;
            set { if (_availableVersion != value) { _availableVersion = value; OnPropertyChanged(nameof(AvailableVersion)); OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(HasUpdateFlag)); } }
        }
        public string Source { get => _source; set { if (_source != value) { _source = value; OnPropertyChanged(nameof(Source)); } } }

        /// <summary>Categoria (Productivity, Development, Games...). Vazio = Uncategorized.</summary>
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } } }
        public string Description { get => _description; set { if (_description != value) { _description = value; OnPropertyChanged(nameof(Description)); } } }
        /// <summary>0..5 rating (0 = sem avaliação). winget não fornece, msstore pode preencher.</summary>
        public double Rating { get => _rating; set { if (Math.Abs(_rating - value) > 0.001) { _rating = Math.Clamp(value, 0, 5); OnPropertyChanged(nameof(Rating)); } } }
        public int RatingCount { get => _ratingCount; set { if (_ratingCount != value) { _ratingCount = value; OnPropertyChanged(nameof(RatingCount)); } } }
        public bool IsFree { get => _isFree; set { if (_isFree != value) { _isFree = value; OnPropertyChanged(nameof(IsFree)); } } }

        public bool HasUpdate => !string.IsNullOrEmpty(AvailableVersion) && !string.Equals(AvailableVersion, Version, StringComparison.OrdinalIgnoreCase);
        public bool HasUpdateFlag => HasUpdate;

        // Helpers para UI: display strings
        public string DisplayVersion => string.IsNullOrEmpty(Version) ? "—" : Version;
        public string DisplayAvailable => string.IsNullOrEmpty(AvailableVersion) ? "" : AvailableVersion;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void RaiseAll() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
    }
}
