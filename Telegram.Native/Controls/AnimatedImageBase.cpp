#include "pch.h"
#include "AnimatedImageBase.h"
#if __has_include("Controls/AnimatedImageBase.g.cpp")
#include "Controls/AnimatedImageBase.g.cpp"
#endif

namespace winrt::Telegram::Native::Controls::implementation
{
    AnimatedImageBase::AnimatedImageBase()
    {
        m_sizeChangedToken = SizeChanged({ this, &AnimatedImageBase::HandleSizeChanged });
    }

    AnimatedImageBase::~AnimatedImageBase()
    {
        SizeChanged(m_sizeChangedToken);
        UnregisterViewportChanged();
    }

    void AnimatedImageBase::OnLoaded()
    {
        if (auto xamlRoot = XamlRoot())
        {
            m_rasterizationScale = xamlRoot.RasterizationScale();
            m_xamlRootChangedToken = xamlRoot.Changed({ this, &AnimatedImageBase::HandleXamlRootChanged });
        }
    }

    void AnimatedImageBase::OnUnloaded()
    {
        XamlRoot().Changed(m_xamlRootChangedToken);
    }

    void AnimatedImageBase::RegisterViewportChanged()
    {
        if (!m_effectiveViewportChangedToken)
        {
            m_effectiveViewportChangedToken = EffectiveViewportChanged({ this, &AnimatedImageBase::HandleEffectiveViewportChanged });
        }
    }

    void AnimatedImageBase::UnregisterViewportChanged()
    {
        if (m_effectiveViewportChangedToken)
        {
            EffectiveViewportChanged(m_effectiveViewportChangedToken);
        }
    }

    void AnimatedImageBase::HandleSizeChanged(winrt::Windows::Foundation::IInspectable const&, winrt::Windows::UI::Xaml::SizeChangedEventArgs const& e)
    {
        overridable().OnSizeChanged(e);
    }

    void AnimatedImageBase::HandleXamlRootChanged(winrt::Windows::UI::Xaml::XamlRoot const& sender, winrt::Windows::UI::Xaml::XamlRootChangedEventArgs const& e)
    {
        auto rasterizationScale = sender.RasterizationScale();
        if (rasterizationScale != m_rasterizationScale)
        {
            m_rasterizationScale = rasterizationScale;
            overridable().OnRasterizationScaleChanged(rasterizationScale);
        }
    }

    void AnimatedImageBase::HandleEffectiveViewportChanged(FrameworkElement const& sender, EffectiveViewportChangedEventArgs const& e)
    {
        auto visible = e.BringIntoViewDistanceX() < sender.ActualWidth() && e.BringIntoViewDistanceY() < sender.ActualHeight();
        if (visible != m_visible)
        {
            m_visible = visible;
            overridable().OnViewportChanged(visible);
        }
    }
}
