using System.Windows;
using System.Windows.Controls;

namespace HUDMind.App;

/// <summary>
/// Mediator coordinating the tactical left sidebar RadioButtons with MainTabs so programmatic
/// switches and manual sidebar clicks always stay synchronized.
/// </summary>
public sealed class HudNavigationMediator
{
    private readonly System.Windows.Controls.TabControl _tabs;
    private readonly IReadOnlyList<System.Windows.Controls.RadioButton> _navButtons;
    private bool _syncing;

    public HudNavigationMediator(
        System.Windows.Controls.TabControl tabs,
        IReadOnlyList<System.Windows.Controls.RadioButton> navButtons)
    {
        _tabs = tabs;
        _navButtons = navButtons;

        for (int i = 0; i < _navButtons.Count; i++)
        {
            int targetIndex = i;
            _navButtons[i].Checked += (_, _) => SelectTab(targetIndex);
        }

        _tabs.SelectionChanged += (_, e) =>
        {
            if (_syncing || !ReferenceEquals(e.OriginalSource, _tabs))
                return;
            SyncSidebarToIndex(_tabs.SelectedIndex);
        };

        SyncSidebarToIndex(_tabs.SelectedIndex);
    }

    public void SelectTab(int index)
    {
        if (_syncing || index < 0 || index >= _tabs.Items.Count)
            return;

        _syncing = true;
        try
        {
            if (_tabs.SelectedIndex != index)
                _tabs.SelectedIndex = index;
            if (index < _navButtons.Count && _navButtons[index].IsChecked != true)
                _navButtons[index].IsChecked = true;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSidebarToIndex(int index)
    {
        if (index < 0 || index >= _navButtons.Count)
            return;

        _syncing = true;
        try
        {
            if (_navButtons[index].IsChecked != true)
                _navButtons[index].IsChecked = true;
        }
        finally
        {
            _syncing = false;
        }
    }
}
