#include "pch.h"
#include "PlaceholderImageHelper.h"
#if __has_include("PlaceholderImageHelper.g.cpp")
#include "PlaceholderImageHelper.g.cpp"
#endif

#include "SVG/nanosvg.h"
#include "StringUtils.h"
#include "Helpers\COMHelper.h"

#include <src\webp\decode.h>
#include <src\webp\demux.h>

#include <shcore.h>
#include <propkey.h>

#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <windows.ui.xaml.media.dxinterop.h>

#include <BufferSurface.h>

using namespace D2D1;
using namespace winrt::Windows::ApplicationModel;
using namespace winrt::Windows::UI::Xaml::Media::Imaging;

namespace winrt::Telegram::Native::implementation
{
    std::mutex PlaceholderImageHelper::s_criticalSection;
    winrt::com_ptr<PlaceholderImageHelper> PlaceholderImageHelper::s_foreground{ nullptr };
    winrt::com_ptr<PlaceholderImageHelper> PlaceholderImageHelper::s_background{ nullptr };

    class CustomEmojiInlineObject
        : public winrt::implements<CustomEmojiInlineObject, IDWriteInlineObject>
    {
        IFACEMETHODIMP2 Draw(
            _In_opt_ void* clientDrawingContext,
            _In_ IDWriteTextRenderer* renderer,
            FLOAT originX,
            FLOAT originY,
            BOOL isSideways,
            BOOL isRightToLeft,
            _In_opt_ IUnknown* clientDrawingEffect
        ) override
        {
            return S_OK;
        }

        IFACEMETHODIMP2 GetMetrics(_Out_ DWRITE_INLINE_OBJECT_METRICS* metrics) override
        {
            DWRITE_INLINE_OBJECT_METRICS inlineMetrics = {};
            inlineMetrics.width = 20;
            inlineMetrics.height = 20;
            inlineMetrics.baseline = 20;
            *metrics = inlineMetrics;
            return S_OK;
        }

        IFACEMETHODIMP2 GetOverhangMetrics(_Out_ DWRITE_OVERHANG_METRICS* overhangs) override
        {
            DWRITE_OVERHANG_METRICS inlineOverhangs = {};
            inlineOverhangs.left = 0;
            inlineOverhangs.top = -2;
            inlineOverhangs.right = 0;
            inlineOverhangs.bottom = -6;
            *overhangs = inlineOverhangs;
            return S_OK;
        }

        IFACEMETHODIMP2 GetBreakConditions(_Out_ DWRITE_BREAK_CONDITION* breakConditionBefore, _Out_ DWRITE_BREAK_CONDITION* breakConditionAfter) override
        {
            *breakConditionBefore = DWRITE_BREAK_CONDITION_CAN_BREAK;
            *breakConditionAfter = DWRITE_BREAK_CONDITION_MAY_NOT_BREAK;
            return S_OK;
        }
    };

    class CustomFontFileEnumerator
        : public winrt::implements<CustomFontFileEnumerator, IDWriteFontFileEnumerator>
    {
        winrt::com_ptr<IDWriteFactory> m_factory;
        std::vector<const wchar_t*> m_filenames;
        int32_t m_index;
        winrt::com_ptr<IDWriteFontFile> m_theFile;

    public:
        CustomFontFileEnumerator(IDWriteFactory* factory, void const* collectionKey, uint32_t collectionKeySize)
            : m_factory()
            , m_index(0)
        {
            auto keys = static_cast<const wchar_t* const*>(collectionKey);

            m_filenames = std::vector<const wchar_t*>(keys, keys + 2);
            m_factory.copy_from(factory);
        }

        IFACEMETHODIMP2 MoveNext(BOOL* hasCurrentFile) override
        {
            if (m_index == m_filenames.size())
            {
                *hasCurrentFile = FALSE;
            }
            else if (SUCCEEDED(m_factory->CreateFontFileReference(m_filenames[m_index++], nullptr, m_theFile.put())))
            {
                *hasCurrentFile = TRUE;
            }
            else
            {
                *hasCurrentFile = FALSE;
            }

            return S_OK;
        }

        IFACEMETHODIMP2 GetCurrentFontFile(IDWriteFontFile** fontFile) override
        {
            m_theFile.copy_to(fontFile);
            return S_OK;
        }
    };



    class CustomFontLoader
        : public winrt::implements<CustomFontLoader, IDWriteFontCollectionLoader>
    {
    public:
        IFACEMETHODIMP2 CreateEnumeratorFromKey(
            IDWriteFactory* factory,
            void const* collectionKey,
            uint32_t collectionKeySize,
            IDWriteFontFileEnumerator** fontFileEnumerator) override
        {
            return ExceptionBoundary(
                [=]
                {
                    auto enumerator = winrt::make_self<CustomFontFileEnumerator>(factory, collectionKey, collectionKeySize);
                    enumerator.as<IDWriteFontFileEnumerator>().copy_to(fontFileEnumerator);
                });
        }
    };

    IBuffer PlaceholderImageHelper::DrawWebP(hstring fileName, int32_t maxWidth, int32_t& pixelWidth, int32_t& pixelHeight) noexcept
    {
        pixelWidth = 0;
        pixelHeight = 0;

        DWORD desired_access = GENERIC_READ;

        // TODO: share mode
        DWORD share_mode = FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE;

        DWORD creation_disposition = OPEN_ALWAYS;

        DWORD native_flags = FILE_FLAG_BACKUP_SEMANTICS;
        //if (flags & Direct) {
        //	native_flags |= FILE_FLAG_WRITE_THROUGH | FILE_FLAG_NO_BUFFERING;
        //}
        //if (flags & WinStat) {
        //	native_flags |= FILE_FLAG_BACKUP_SEMANTICS;
        //}
        CREATEFILE2_EXTENDED_PARAMETERS extended_parameters;
        std::memset(&extended_parameters, 0, sizeof(extended_parameters));
        extended_parameters.dwSize = sizeof(extended_parameters);
        extended_parameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
        extended_parameters.dwFileFlags = native_flags;
        HANDLE handle = CreateFile2FromAppW(fileName.c_str(), desired_access, share_mode, creation_disposition, &extended_parameters);

        if (handle == INVALID_HANDLE_VALUE)
        {
            return nullptr;
        }

        LARGE_INTEGER pFileSize;
        if (!GetFileSizeEx(handle, &pFileSize))
        {
            CloseHandle(handle);
            return nullptr;
        }

        size_t length = static_cast<size_t>(pFileSize.QuadPart);
        char* buffer = (char*)malloc(length);

        DWORD numberOfBytesRead;
        if (!ReadFile(handle, buffer, length, &numberOfBytesRead, NULL))
        {
            CloseHandle(handle);
            return nullptr;
        }

        CloseHandle(handle);

        WebPData webPData;
        webPData.bytes = (uint8_t*)buffer;
        webPData.size = length;

        auto spDemuxer = std::unique_ptr<WebPDemuxer, decltype(&WebPDemuxDelete)>
        {
            WebPDemux(&webPData),
            WebPDemuxDelete
        };
        if (!spDemuxer)
        {
            //throw ref new InvalidArgumentException(ref new String(L"Failed to create demuxer"));
            free(buffer);
            return nullptr;
        }

        IBuffer surface;
        WebPIterator iter;
        if (WebPDemuxGetFrame(spDemuxer.get(), 1, &iter))
        {
            WebPDecoderConfig config;
            int ret = WebPInitDecoderConfig(&config);
            if (!ret)
            {
                //throw ref new FailureException(ref new String(L"WebPInitDecoderConfig failed"));
                free(buffer);
                return nullptr;
            }

            ret = (WebPGetFeatures(iter.fragment.bytes, iter.fragment.size, &config.input) == VP8_STATUS_OK);
            if (!ret)
            {
                //throw ref new FailureException(ref new String(L"WebPGetFeatures failed"));
                free(buffer);
                return nullptr;
            }

            int width = iter.width;
            int height = iter.height;

            if (iter.width > maxWidth || iter.height > maxWidth)
            {
                auto ratioX = (double)maxWidth / iter.width;
                auto ratioY = (double)maxWidth / iter.height;
                auto ratio = std::min(ratioX, ratioY);

                width = (int)(iter.width * ratio);
                height = (int)(iter.height * ratio);
            }

            pixelWidth = width;
            pixelHeight = height;

            surface = Telegram::Native::BufferSurface::Create(width * 4 * height);
            auto pixels = surface.data();
            //uint8_t* pixels = new uint8_t[(width * 4) * height];

            if (width != iter.width || height != iter.height)
            {
                config.options.scaled_width = width;
                config.options.scaled_height = height;
                config.options.use_scaling = 1;
                config.options.no_fancy_upsampling = 1;
            }

            config.output.colorspace = MODE_bgrA;
            config.output.is_external_memory = 1;
            config.output.u.RGBA.rgba = pixels;
            config.output.u.RGBA.stride = width * 4;
            config.output.u.RGBA.size = (width * 4) * height;

            ret = WebPDecode(iter.fragment.bytes, iter.fragment.size, &config);

            if (ret != VP8_STATUS_OK)
            {
                //throw ref new FailureException(ref new String(L"Failed to decode frame"));
                //delete[] pixels;

                free(buffer);
                return nullptr;
            }

            //delete[] pixels;

        }

        free(buffer);
        return surface;
    }

    bool PlaceholderImageHelper::IsWebP(hstring fileName, int32_t& pixelWidth, int32_t& pixelHeight) noexcept
    {
        pixelWidth = 0;
        pixelHeight = 0;

        DWORD desired_access = GENERIC_READ;

        // TODO: share mode
        DWORD share_mode = FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE;

        DWORD creation_disposition = OPEN_ALWAYS;

        DWORD native_flags = FILE_FLAG_BACKUP_SEMANTICS;
        //if (flags & Direct) {
        //	native_flags |= FILE_FLAG_WRITE_THROUGH | FILE_FLAG_NO_BUFFERING;
        //}
        //if (flags & WinStat) {
        //	native_flags |= FILE_FLAG_BACKUP_SEMANTICS;
        //}
        CREATEFILE2_EXTENDED_PARAMETERS extended_parameters;
        std::memset(&extended_parameters, 0, sizeof(extended_parameters));
        extended_parameters.dwSize = sizeof(extended_parameters);
        extended_parameters.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
        extended_parameters.dwFileFlags = native_flags;
        HANDLE handle = CreateFile2FromAppW(fileName.c_str(), desired_access, share_mode, creation_disposition, &extended_parameters);

        if (handle == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        LARGE_INTEGER pFileSize;
        if (!GetFileSizeEx(handle, &pFileSize))
        {
            CloseHandle(handle);
            return false;
        }

        size_t length = static_cast<size_t>(pFileSize.QuadPart);
        char* buffer = (char*)malloc(length);

        DWORD numberOfBytesRead;
        if (!ReadFile(handle, buffer, length, &numberOfBytesRead, NULL))
        {
            CloseHandle(handle);
            return false;
        }

        CloseHandle(handle);

        WebPData webPData;
        webPData.bytes = (uint8_t*)buffer;
        webPData.size = length;

        auto spDemuxer = std::unique_ptr<WebPDemuxer, decltype(&WebPDemuxDelete)>
        {
            WebPDemux(&webPData),
            WebPDemuxDelete
        };
        if (!spDemuxer)
        {
            //throw ref new InvalidArgumentException(ref new String(L"Failed to create demuxer"));
            free(buffer);
            return false;
        }

        IBuffer surface;
        WebPIterator iter;
        if (WebPDemuxGetFrame(spDemuxer.get(), 1, &iter))
        {
            WebPDecoderConfig config;
            int ret = WebPInitDecoderConfig(&config);
            if (!ret)
            {
                //throw ref new FailureException(ref new String(L"WebPInitDecoderConfig failed"));
                free(buffer);
                return false;
            }

            ret = (WebPGetFeatures(iter.fragment.bytes, iter.fragment.size, &config.input) == VP8_STATUS_OK);
            if (!ret)
            {
                //throw ref new FailureException(ref new String(L"WebPGetFeatures failed"));
                free(buffer);
                return false;
            }

            pixelWidth = iter.width;
            pixelHeight = iter.height;
        }

        free(buffer);
        return true;
    }

    winrt::Windows::Foundation::IAsyncAction PlaceholderImageHelper::DrawSvgAsync(hstring path, Color foreground, IRandomAccessStream randomAccessStream, double dpi)
    {
        winrt::apartment_context ui_thread;
        co_await winrt::resume_background();

        Windows::Foundation::Size size;
        DrawSvg(path, foreground, randomAccessStream, dpi, size);
        randomAccessStream.Seek(0);

        co_await ui_thread;
    }

    winrt::Telegram::Native::SurfaceImage PlaceholderImageHelper::Create(int32_t pixelWidth, int32_t pixelHeight)
    {
        std::lock_guard const guard(m_criticalSection);

        auto surface = winrt::make_self<SurfaceImage>(m_d2dDevice.get(), pixelWidth, pixelHeight);
        return surface.as<winrt::Telegram::Native::SurfaceImage>();
    }

    HRESULT PlaceholderImageHelper::Invalidate(winrt::Telegram::Native::SurfaceImage imageSource, IBuffer buffer)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        com_ptr<SurfaceImage> source = imageSource.as<SurfaceImage>();
        int32_t pixelWidth = source->m_pixelWidth;
        int32_t pixelHeight = source->m_pixelHeight;
        winrt::com_ptr<ISurfaceImageSourceNativeWithD2D> native = source->m_native;

        D2D1_SIZE_U size{ pixelWidth, pixelHeight };
        D2D1_RECT_U rect{ 0, 0, pixelWidth, pixelHeight };
        RECT updateRect{ 0, 0, pixelWidth, pixelHeight };
        POINT offset{ 0, 0 };

        com_ptr<ID2D1DeviceContext> d2d1DeviceContext;
        result = native->BeginDraw(updateRect, __uuidof(ID2D1DeviceContext), d2d1DeviceContext.put_void(), &offset);

        if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET)
        {
            ReturnIfFailed(result, CreateDeviceResources());
            ReturnIfFailed(result, source->CreateDeviceResources(m_d2dDevice.get()));
            return Invalidate(imageSource, buffer);
        }

        com_ptr<ID2D1Bitmap1> bitmap;
        D2D1_BITMAP_PROPERTIES1 properties = { { DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED }, 96, 96, D2D1_BITMAP_OPTIONS_NONE, 0 };
        CleanupIfFailed(result, d2d1DeviceContext->CreateBitmap(size, buffer.data(), pixelWidth * 4, &properties, bitmap.put()));

        d2d1DeviceContext->SetTransform(D2D1::Matrix3x2F::Translation(offset.x, offset.y));
        d2d1DeviceContext->Clear(D2D1::ColorF(0, 0, 0, 0));
        d2d1DeviceContext->DrawBitmap(bitmap.get());

    Cleanup:
        return native->EndDraw();
    }

    HRESULT PlaceholderImageHelper::DrawSvg(hstring path, Color foreground, IRandomAccessStream randomAccessStream, double dpi, Windows::Foundation::Size& size)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        auto data = winrt::to_string(path);

        struct NSVGimage* image;
        image = nsvgParse((char*)data.c_str(), "px", 96);

        auto unique = std::shared_ptr<NSVGimage>(image, [](NSVGimage* p)
            {
                nsvgDelete(p);
            });

        auto imageWidth = image->width * dpi;
        auto imageHeight = image->height * dpi;
        size = Windows::Foundation::Size(imageWidth, imageHeight);

        winrt::com_ptr<ID2D1Bitmap1> targetBitmap;
        D2D1_BITMAP_PROPERTIES1 properties = { { DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED }, 96, 96, D2D1_BITMAP_OPTIONS_TARGET, 0 };
        ReturnIfFailed(result, m_d2dContext->CreateBitmap(D2D1_SIZE_U{ (uint32_t)imageWidth, (uint32_t)imageHeight }, nullptr, 0, &properties, targetBitmap.put()));

        m_d2dContext->SetTarget(targetBitmap.get());
        m_d2dContext->BeginDraw();
        m_d2dContext->Clear(D2D1::ColorF(0, 0, 0, 0));
        m_d2dContext->SetTransform(D2D1::Matrix3x2F::Scale(1 * dpi, 1 * dpi));

        winrt::com_ptr<ID2D1SolidColorBrush> blackBrush;
        ReturnIfFailed(result, m_d2dContext->CreateSolidColorBrush(
            D2D1::ColorF(foreground.R / 255.0f, foreground.G / 255.0f, foreground.B / 255.0f, foreground.A / 255.0f), blackBrush.put()));

        for (auto shape = image->shapes; shape != NULL; shape = shape->next)
        {
            if (!(shape->flags & NSVG_FLAGS_VISIBLE) || (shape->fill.type == NSVG_PAINT_NONE && shape->stroke.type == NSVG_PAINT_NONE))
            {
                continue;
            }

            blackBrush->SetOpacity(shape->opacity);

            winrt::com_ptr<ID2D1PathGeometry1> geometry;
            ReturnIfFailed(result, m_d2dFactory->CreatePathGeometry(geometry.put()));

            winrt::com_ptr<ID2D1GeometrySink> sink;
            ReturnIfFailed(result, geometry->Open(sink.put()));

            for (NSVGpath* path = shape->paths; path != NULL; path = path->next)
            {
                sink->BeginFigure({ path->pts[0], path->pts[1] }, D2D1_FIGURE_BEGIN_FILLED);

                for (int i = 0; i < path->npts - 1; i += 3)
                {
                    float* p = &path->pts[i * 2];
                    sink->AddBezier({ { p[2], p[3] }, { p[4], p[5] }, { p[6], p[7] } });
                }

                sink->EndFigure(path->closed ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            }

            ReturnIfFailed(result, sink->Close());

            if (shape->fill.type != NSVG_PAINT_NONE)
            {
                switch (shape->fillRule)
                {
                case NSVG_FILLRULE_EVENODD:
                    sink->SetFillMode(D2D1_FILL_MODE_ALTERNATE);
                    break;
                default:
                    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
                    break;
                }

                m_d2dContext->FillGeometry(geometry.get(), blackBrush.get());
            }

            if (shape->stroke.type != NSVG_PAINT_NONE)
            {
                D2D1_STROKE_STYLE_PROPERTIES1 strokeProperties{};
                strokeProperties.miterLimit = shape->miterLimit;

                switch (shape->strokeLineCap)
                {
                case NSVG_CAP_BUTT:
                    strokeProperties.startCap = strokeProperties.endCap = D2D1_CAP_STYLE_FLAT;
                    break;
                case NSVG_CAP_ROUND:
                    strokeProperties.startCap = strokeProperties.endCap = D2D1_CAP_STYLE_ROUND;
                    break;
                case NSVG_CAP_SQUARE:
                    strokeProperties.startCap = strokeProperties.endCap = D2D1_CAP_STYLE_SQUARE;
                    break;
                default:
                    break;
                }

                switch (shape->strokeLineJoin)
                {
                case NSVG_JOIN_BEVEL:
                    strokeProperties.lineJoin = D2D1_LINE_JOIN_BEVEL;
                    break;
                case NSVG_JOIN_MITER:
                    strokeProperties.lineJoin = D2D1_LINE_JOIN_MITER;
                    break;
                case NSVG_JOIN_ROUND:
                    strokeProperties.lineJoin = D2D1_LINE_JOIN_ROUND;
                    break;
                default:
                    break;
                }

                winrt::com_ptr<ID2D1StrokeStyle1> strokeStyle;
                ReturnIfFailed(result, m_d2dFactory->CreateStrokeStyle(strokeProperties, NULL, 0, strokeStyle.put()));

                m_d2dContext->DrawGeometry(geometry.get(), blackBrush.get(), shape->strokeWidth, strokeStyle.get());
            }
        }

        m_d2dContext->SetTransform(D2D1::Matrix3x2F::Identity());

        if ((result = m_d2dContext->EndDraw()) == D2DERR_RECREATE_TARGET)
        {
            ReturnIfFailed(result, CreateDeviceResources());
            return DrawSvg(path, foreground, randomAccessStream, dpi, size);
        }

        return SaveImageToStream(targetBitmap.get(), GUID_ContainerFormatPng, randomAccessStream);
    }

    HRESULT PlaceholderImageHelper::DrawThumbnailPlaceholder(hstring fileName, float blurAmount, IRandomAccessStream randomAccessStream)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        HANDLE file = CreateFile2FromAppW(fileName.data(), GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, nullptr);

        if (file == INVALID_HANDLE_VALUE)
        {
            return ERROR_FILE_NOT_FOUND;
        }

        winrt::com_ptr<IWICBitmapDecoder> wicBitmapDecoder;
        //ReturnIfFailed(result, m_wicFactory->CreateDecoderFromFilename(fileName->Data(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &wicBitmapDecoder));
        ReturnIfFailed(result, m_wicFactory->CreateDecoderFromFileHandle(reinterpret_cast<ULONG_PTR>(file), nullptr, WICDecodeMetadataCacheOnLoad, wicBitmapDecoder.put()));

        winrt::com_ptr<IWICBitmapFrameDecode> wicFrameDecode;
        ReturnIfFailed(result, wicBitmapDecoder->GetFrame(0, wicFrameDecode.put()));

        winrt::com_ptr<IWICFormatConverter> wicFormatConverter;
        ReturnIfFailed(result, m_wicFactory->CreateFormatConverter(wicFormatConverter.put()));
        ReturnIfFailed(result, wicFormatConverter->Initialize(wicFrameDecode.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0.f, WICBitmapPaletteTypeCustom));

        ReturnIfFailed(result, InternalDrawThumbnailPlaceholder(wicFormatConverter.get(), blurAmount, randomAccessStream, false));

        CloseHandle(file);

        return result;
    }

    HRESULT PlaceholderImageHelper::DrawThumbnailPlaceholder(IVector<uint8_t> bytes, float blurAmount, IRandomAccessStream randomAccessStream)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<IStream> stream;
        ReturnIfFailed(result, CreateStreamOverRandomAccessStream(winrt::get_unknown(randomAccessStream), IID_PPV_ARGS(&stream)));

        auto yolo = std::vector<byte>(bytes.begin(), bytes.end());

        ReturnIfFailed(result, stream->Write(yolo.data(), bytes.Size(), nullptr));
        ReturnIfFailed(result, stream->Seek({ 0 }, STREAM_SEEK_SET, nullptr));

        winrt::com_ptr<IWICBitmapDecoder> wicBitmapDecoder;
        ReturnIfFailed(result, m_wicFactory->CreateDecoderFromStream(stream.get(), nullptr, WICDecodeMetadataCacheOnLoad, wicBitmapDecoder.put()));

        winrt::com_ptr<IWICBitmapFrameDecode> wicFrameDecode;
        ReturnIfFailed(result, wicBitmapDecoder->GetFrame(0, wicFrameDecode.put()));

        winrt::com_ptr<IWICFormatConverter> wicFormatConverter;
        ReturnIfFailed(result, m_wicFactory->CreateFormatConverter(wicFormatConverter.put()));
        ReturnIfFailed(result, wicFormatConverter->Initialize(wicFrameDecode.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0.f, WICBitmapPaletteTypeCustom));

        ReturnIfFailed(result, InternalDrawThumbnailPlaceholder(wicFormatConverter.get(), blurAmount, randomAccessStream, true));

        return result;
    }
    HRESULT PlaceholderImageHelper::DrawThumbnailPlaceholder(IVector<uint8_t> bytes, float blurAmount, IBuffer randomAccessStream)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<IStream> stream;
        ReturnIfFailed(result, CreateStreamOverRandomAccessStream(winrt::get_unknown(randomAccessStream), IID_PPV_ARGS(&stream)));

        auto yolo = std::vector<byte>(bytes.begin(), bytes.end());

        ReturnIfFailed(result, stream->Write(yolo.data(), bytes.Size(), nullptr));
        ReturnIfFailed(result, stream->Seek({ 0 }, STREAM_SEEK_SET, nullptr));

        winrt::com_ptr<IWICBitmapDecoder> wicBitmapDecoder;
        ReturnIfFailed(result, m_wicFactory->CreateDecoderFromStream(stream.get(), nullptr, WICDecodeMetadataCacheOnLoad, wicBitmapDecoder.put()));

        winrt::com_ptr<IWICBitmapFrameDecode> wicFrameDecode;
        ReturnIfFailed(result, wicBitmapDecoder->GetFrame(0, wicFrameDecode.put()));

        winrt::com_ptr<IWICFormatConverter> wicFormatConverter;
        ReturnIfFailed(result, m_wicFactory->CreateFormatConverter(wicFormatConverter.put()));
        ReturnIfFailed(result, wicFormatConverter->Initialize(wicFrameDecode.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0.f, WICBitmapPaletteTypeCustom));

        ReturnIfFailed(result, InternalDrawThumbnailPlaceholder(wicFormatConverter.get(), blurAmount, randomAccessStream, true));

        return result;
    }


    HRESULT PlaceholderImageHelper::InternalDrawThumbnailPlaceholder(IWICBitmapSource* wicBitmapSource, float blurAmount, IRandomAccessStream randomAccessStream, bool minithumbnail)
    {
        HRESULT result;
        winrt::com_ptr<ID2D1ImageSourceFromWic> imageSource;
        ReturnIfFailed(result, m_d2dContext->CreateImageSourceFromWic(wicBitmapSource, imageSource.put()));

        D2D1_SIZE_U size;
        ReturnIfFailed(result, wicBitmapSource->GetSize(&size.width, &size.height));

        //if (minithumbnail) {
        //	size.width *= 2;
        //	size.height *= 2;
        //}

        winrt::com_ptr<ID2D1Bitmap1> targetBitmap;
        D2D1_BITMAP_PROPERTIES1 properties = { { DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE }, 96, 96, D2D1_BITMAP_OPTIONS_TARGET, 0 };
        ReturnIfFailed(result, m_d2dContext->CreateBitmap(size, nullptr, 0, &properties, targetBitmap.put()));

        //winrt::com_ptr<ID2D1Effect> scaleEffect;
        //ReturnIfFailed(result, m_d2dContext->CreateEffect(CLSID_D2D1Scale, scaleEffect.put()));
        //ReturnIfFailed(result, scaleEffect->SetValue(D2D1_SCALE_PROP_SCALE, D2D1_VECTOR_2F({ 2, 2 })));
        //ReturnIfFailed(result, scaleEffect->SetValue(D2D1_SCALE_PROP_INTERPOLATION_MODE, D2D1_SCALE_INTERPOLATION_MODE_NEAREST_NEIGHBOR));
        //scaleEffect->SetInput(0, imageSource.get());

        //winrt::com_ptr<ID2D1Image> test;
        //scaleEffect->SetInput(0, imageSource.get());
        //scaleEffect->GetOutput(test.put());

        ReturnIfFailed(result, m_gaussianBlurEffect->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, blurAmount));

        //m_gaussianBlurEffect->SetInput(0, test.get());
        m_gaussianBlurEffect->SetInput(0, imageSource.get());

        m_d2dContext->SetTarget(targetBitmap.get());
        m_d2dContext->BeginDraw();
        //m_d2dContext->SetTransform(D2D1::Matrix3x2F::Identity());
        m_d2dContext->Clear(D2D1::ColorF(ColorF::Black, 0.0f));
        m_d2dContext->DrawImage(m_gaussianBlurEffect.get());

        if ((result = m_d2dContext->EndDraw()) == D2DERR_RECREATE_TARGET)
        {
            ReturnIfFailed(result, CreateDeviceResources());
            return InternalDrawThumbnailPlaceholder(wicBitmapSource, blurAmount, randomAccessStream, minithumbnail);
        }

        return SaveImageToStream(targetBitmap.get(), GUID_ContainerFormatPng, randomAccessStream);
    }

    HRESULT PlaceholderImageHelper::InternalDrawThumbnailPlaceholder(IWICBitmapSource* wicBitmapSource, float blurAmount, IBuffer randomAccessStream, bool minithumbnail)
    {
        HRESULT result;
        winrt::com_ptr<ID2D1ImageSourceFromWic> imageSource;
        ReturnIfFailed(result, m_d2dContext->CreateImageSourceFromWic(wicBitmapSource, imageSource.put()));

        D2D1_SIZE_U size;
        ReturnIfFailed(result, wicBitmapSource->GetSize(&size.width, &size.height));

        //if (minithumbnail) {
        //	size.width *= 2;
        //	size.height *= 2;
        //}

        winrt::com_ptr<ID2D1Bitmap1> targetBitmap;
        D2D1_BITMAP_PROPERTIES1 properties = { { DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE }, 96, 96, D2D1_BITMAP_OPTIONS_TARGET, 0 };
        ReturnIfFailed(result, m_d2dContext->CreateBitmap(size, nullptr, 0, &properties, targetBitmap.put()));

        //winrt::com_ptr<ID2D1Effect> scaleEffect;
        //ReturnIfFailed(result, m_d2dContext->CreateEffect(CLSID_D2D1Scale, scaleEffect.put()));
        //ReturnIfFailed(result, scaleEffect->SetValue(D2D1_SCALE_PROP_SCALE, D2D1_VECTOR_2F({ 2, 2 })));
        //ReturnIfFailed(result, scaleEffect->SetValue(D2D1_SCALE_PROP_INTERPOLATION_MODE, D2D1_SCALE_INTERPOLATION_MODE_NEAREST_NEIGHBOR));
        //scaleEffect->SetInput(0, imageSource.get());

        //winrt::com_ptr<ID2D1Image> test;
        //scaleEffect->SetInput(0, imageSource.get());
        //scaleEffect->GetOutput(test.put());

        ReturnIfFailed(result, m_gaussianBlurEffect->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, blurAmount));

        //m_gaussianBlurEffect->SetInput(0, test.get());
        m_gaussianBlurEffect->SetInput(0, imageSource.get());

        m_d2dContext->SetTarget(targetBitmap.get());
        m_d2dContext->BeginDraw();
        //m_d2dContext->SetTransform(D2D1::Matrix3x2F::Identity());
        m_d2dContext->Clear(D2D1::ColorF(ColorF::Black, 0.0f));
        m_d2dContext->DrawImage(m_gaussianBlurEffect.get());

        if ((result = m_d2dContext->EndDraw()) == D2DERR_RECREATE_TARGET)
        {
            ReturnIfFailed(result, CreateDeviceResources());
            return InternalDrawThumbnailPlaceholder(wicBitmapSource, blurAmount, randomAccessStream, minithumbnail);
        }

        winrt::com_ptr<ID2D1Bitmap1> readBitmap;
        D2D1_BITMAP_PROPERTIES1 properties2 = { { DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE }, 96, 96, D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW, 0 };
        ReturnIfFailed(result, m_d2dContext->CreateBitmap(size, nullptr, 0, &properties2, readBitmap.put()));

        D2D1_POINT_2U origin{ 0, 0 };
        D2D1_RECT_U source{ 0, 0, size.width, size.height };
        D2D1_MAPPED_RECT map;
        ReturnIfFailed(result, readBitmap->CopyFromBitmap(&origin, targetBitmap.get(), &source));
        ReturnIfFailed(result, readBitmap->Map(D2D1_MAP_OPTIONS_READ, &map));

        memcpy(randomAccessStream.data(), map.bits, randomAccessStream.Length());

        return readBitmap->Unmap();
        //return SaveImageToStream(targetBitmap.get(), GUID_ContainerFormatPng, randomAccessStream);
    }

    PlaceholderImageHelper::PlaceholderImageHelper()
    {
        winrt::check_hresult(CreateDeviceIndependentResources());
        winrt::check_hresult(CreateDeviceResources());
    }

    HRESULT PlaceholderImageHelper::CreateDeviceIndependentResources()
    {
        HRESULT result;
        D2D1_FACTORY_OPTIONS options = {};
        ReturnIfFailed(result, D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, __uuidof(ID2D1Factory1), &options, m_d2dFactory.put_void()));
        ReturnIfFailed(result, CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&m_wicFactory)));
        ReturnIfFailed(result, DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)m_dwriteFactory.put()));

        m_customEmoji = winrt::make_self<CustomEmojiInlineObject>();

        hstring path1 = Package::Current().InstalledLocation().Path() + L"\\Assets\\Fonts\\Telegram.ttf";
        hstring path2 = Package::Current().InstalledLocation().Path() + L"\\Assets\\Emoji\\apple.ttf";

        auto keySize = path1.size() + path2.size();
        const wchar_t* keys[]
        {
            path1.c_str(),
            path2.c_str()
        };

        m_customLoader = winrt::make_self<CustomFontLoader>();
        ReturnIfFailed(result, m_dwriteFactory->RegisterFontCollectionLoader(m_customLoader.get()));
        ReturnIfFailed(result, m_dwriteFactory->CreateCustomFontCollection(m_customLoader.get(), keys, keySize, m_fontCollection.put()));
        ReturnIfFailed(result, m_dwriteFactory->GetSystemFontCollection(m_systemCollection.put()));

        return S_OK;
    }

    HRESULT PlaceholderImageHelper::CreateDeviceResources()
    {
        HRESULT result;
        UINT creationFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        winrt::com_ptr<ID3D11DeviceContext> context;
        if (FAILED(D3D11CreateDevice(nullptr,               // specify null to use the default adapter
            D3D_DRIVER_TYPE_HARDWARE, 0,
            creationFlags,									// optionally set debug and Direct2D compatibility flags
            NULL,											// list of feature levels this app can support
            0,												// number of possible feature levels
            D3D11_SDK_VERSION,
            m_d3dDevice.put(),								// returns the Direct3D device created
            &m_featureLevel,								// returns feature level of device created
            context.put()									// returns the device immediate context
        )))
        {
            // Try again using WARP (software rendering)
            ReturnIfFailed(result, D3D11CreateDevice(nullptr,
                D3D_DRIVER_TYPE_WARP, 0,
                creationFlags,
                NULL,
                0,
                D3D11_SDK_VERSION,
                m_d3dDevice.put(),
                &m_featureLevel,
                context.put()
            ));
        }

        winrt::com_ptr<IDXGIDevice> dxgiDevice = m_d3dDevice.as<IDXGIDevice>();
        ReturnIfFailed(result, m_d2dFactory->CreateDevice(dxgiDevice.get(), m_d2dDevice.put()));

        winrt::com_ptr<ID2D1DeviceContext> d2dContext;
        ReturnIfFailed(result, m_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, d2dContext.put()));
        m_d2dContext = d2dContext.as<ID2D1DeviceContext2>();

        ReturnIfFailed(result, m_d2dContext->CreateEffect(CLSID_D2D1GaussianBlur, m_gaussianBlurEffect.put()));
        ReturnIfFailed(result, m_gaussianBlurEffect->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, D2D1_BORDER_MODE_HARD));

        m_d2dContext->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

        return m_wicFactory->CreateImageEncoder(m_d2dDevice.get(), m_imageEncoder.put());
    }

    HRESULT PlaceholderImageHelper::CreateTextFormat(double fontSize)
    {
        if (m_appleFormat != nullptr && fontSize == m_appleFormat->GetFontSize())
        {
            return S_OK;
        }

        HRESULT result;
        ReturnIfFailed(result, m_dwriteFactory->CreateTextFormat(
            L"Segoe UI Emoji",						// font family name
            m_fontCollection.get(),			        // system font collection
            DWRITE_FONT_WEIGHT_NORMAL,				// font weight 
            DWRITE_FONT_STYLE_NORMAL,				// font style
            DWRITE_FONT_STRETCH_NORMAL,				// default font stretch
            fontSize,								// font size
            L"",									// locale name
            m_appleFormat.put()
        ));
        ReturnIfFailed(result, m_appleFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING));
        ReturnIfFailed(result, m_appleFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
        return result;
    }

    winrt::Telegram::Native::TextFormat PlaceholderImageHelper::CreateTextFormat2(hstring text, IVector<TextEntity> entities, double fontSize, double width)
    {
        winrt::com_ptr<TextFormat> textFormat;
        CreateTextFormatImpl(text, entities, fontSize, width, textFormat);
        return textFormat.as<winrt::Telegram::Native::TextFormat>();
    }

    HRESULT PlaceholderImageHelper::CreateTextFormatImpl(hstring text, IVector<TextEntity> entities, double fontSize, double width, winrt::com_ptr<TextFormat>& textFormat2)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        //ReturnIfFailed(result, CreateTextFormat(fontSize));

        winrt::com_ptr<IDWriteTextFormat> textFormat;
        ReturnIfFailed(result, m_dwriteFactory->CreateTextFormat(
            L"Segoe UI Emoji",						// font family name
            m_fontCollection.get(),			        // system font collection
            DWRITE_FONT_WEIGHT_NORMAL,				// font weight 
            DWRITE_FONT_STYLE_NORMAL,				// font style
            DWRITE_FONT_STRETCH_NORMAL,				// default font stretch
            fontSize,								// font size
            L"",									// locale name
            textFormat.put()
        ));
        ReturnIfFailed(result, textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING));
        ReturnIfFailed(result, textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));

        winrt::com_ptr<IDWriteTextLayout> textLayout;
        ReturnIfFailed(result, m_dwriteFactory->CreateTextLayout(
            text.data(),					// The string to be laid out and formatted.
            text.size(),        			// The length of the string.
            textFormat.get(),			    // The text format to apply to the string (contains font information, etc).
            width,							// The width of the layout box.
            INFINITY,						// The height of the layout box.
            textLayout.put()				// The IDWriteTextLayout interface pointer.
        ));

        for (const TextEntity& entity : entities)
        {
            UINT32 startPosition = entity.Offset();
            UINT32 length = entity.Length();
            auto name = winrt::get_class_name(entity.Type());

            if (name == winrt::name_of<TextEntityTypeBold>())
            {
                ReturnIfFailed(result, textLayout->SetFontWeight(DWRITE_FONT_WEIGHT_SEMI_BOLD, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeItalic>())
            {
                ReturnIfFailed(result, textLayout->SetFontStyle(DWRITE_FONT_STYLE_ITALIC, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeStrikethrough>())
            {
                ReturnIfFailed(result, textLayout->SetStrikethrough(TRUE, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeUnderline>())
            {
                ReturnIfFailed(result, textLayout->SetUnderline(TRUE, { startPosition, length }));
            }
            //else if (name == winrt::name_of<TextEntityTypeCustomEmoji>())
            //{
            //    textLayout->SetInlineObject(m_customEmoji.get(), { startPosition, length });
            //}
            else if (name == winrt::name_of<TextEntityTypeCode>() || name == winrt::name_of<TextEntityTypePre>() || name == winrt::name_of<TextEntityTypePreCode>())
            {
                ReturnIfFailed(result, textLayout->SetFontCollection(m_systemCollection.get(), { startPosition, length }));
                ReturnIfFailed(result, textLayout->SetFontFamilyName(L"Consolas", { startPosition, length }));
            }
        }

        textFormat2 = winrt::make_self<TextFormat>(textLayout, text.size(), fontSize, width);
        return result;
    }

    float2 PlaceholderImageHelper::ContentEnd(hstring text, IVector<TextEntity> entities, double fontSize, double width)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        //ReturnIfFailed(result, CreateTextFormat(fontSize));

        winrt::com_ptr<IDWriteTextFormat> textFormat;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextFormat(
            L"Segoe UI Emoji",						// font family name
            m_fontCollection.get(),			        // system font collection
            DWRITE_FONT_WEIGHT_NORMAL,				// font weight 
            DWRITE_FONT_STYLE_NORMAL,				// font style
            DWRITE_FONT_STRETCH_NORMAL,				// default font stretch
            fontSize,								// font size
            L"",									// locale name
            textFormat.put()
        ));
        ReturnDefaultIfFailed(result, textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING));
        ReturnDefaultIfFailed(result, textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));

        winrt::com_ptr<IDWriteTextLayout> textLayout;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextLayout(
            text.data(),					// The string to be laid out and formatted.
            text.size(),        			// The length of the string.
            textFormat.get(),			    // The text format to apply to the string (contains font information, etc).
            width,							// The width of the layout box.
            INFINITY,						// The height of the layout box.
            textLayout.put()				// The IDWriteTextLayout interface pointer.
        ));

        for (const TextEntity& entity : entities)
        {
            UINT32 startPosition = entity.Offset();
            UINT32 length = entity.Length();
            auto name = winrt::get_class_name(entity.Type());

            if (name == winrt::name_of<TextEntityTypeBold>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontWeight(DWRITE_FONT_WEIGHT_SEMI_BOLD, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeItalic>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontStyle(DWRITE_FONT_STYLE_ITALIC, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeStrikethrough>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetStrikethrough(TRUE, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeUnderline>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetUnderline(TRUE, { startPosition, length }));
            }
            //else if (name == winrt::name_of<TextEntityTypeCustomEmoji>())
            //{
            //    textLayout->SetInlineObject(m_customEmoji.get(), { startPosition, length });
            //}
            else if (name == winrt::name_of<TextEntityTypeCode>() || name == winrt::name_of<TextEntityTypePre>() || name == winrt::name_of<TextEntityTypePreCode>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontCollection(m_systemCollection.get(), { startPosition, length }));
                ReturnDefaultIfFailed(result, textLayout->SetFontFamilyName(L"Consolas", { startPosition, length }));
            }
        }

        DWRITE_TEXT_METRICS metrics;
        ReturnDefaultIfFailed(result, textLayout->GetMetrics(&metrics));

        BOOL isTrailingHit;
        BOOL isInside;
        DWRITE_HIT_TEST_METRICS hitTestMetrics;
        ReturnDefaultIfFailed(result, textLayout->HitTestPoint(metrics.width, metrics.height, &isTrailingHit, &isInside, &hitTestMetrics));

        return float2(hitTestMetrics.left + hitTestMetrics.width, hitTestMetrics.top + hitTestMetrics.height);
    }

    IVector<Windows::Foundation::Rect> PlaceholderImageHelper::LineMetrics(hstring text, IVector<TextEntity> entities, double fontSize, double width, bool rtl)
    {
        return RangeMetrics(text, 0, text.size(), entities, fontSize, width, rtl, true);
    }

    IVector<Windows::Foundation::Rect> PlaceholderImageHelper::RangeMetrics(hstring text, int32_t offset, int32_t length, IVector<TextEntity> entities, double fontSize, double width, bool rtl, bool wrap)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        //ReturnIfFailed(result, CreateTextFormat(fontSize));
        //ReturnIfFailed(result, m_appleFormat->SetReadingDirection(rtl ? DWRITE_READING_DIRECTION_RIGHT_TO_LEFT : DWRITE_READING_DIRECTION_LEFT_TO_RIGHT));

        winrt::com_ptr<IDWriteTextFormat> textFormat;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextFormat(
            L"Segoe UI Emoji",						// font family name
            m_fontCollection.get(),			        // system font collection
            DWRITE_FONT_WEIGHT_NORMAL,				// font weight 
            DWRITE_FONT_STYLE_NORMAL,				// font style
            DWRITE_FONT_STRETCH_NORMAL,				// default font stretch
            fontSize,								// font size
            L"",									// locale name
            textFormat.put()
        ));
        ReturnDefaultIfFailed(result, textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING));
        ReturnDefaultIfFailed(result, textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
        ReturnDefaultIfFailed(result, textFormat->SetReadingDirection(rtl ? DWRITE_READING_DIRECTION_RIGHT_TO_LEFT : DWRITE_READING_DIRECTION_LEFT_TO_RIGHT));
        ReturnDefaultIfFailed(result, textFormat->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING_NO_WRAP));

        if (wrap)
        {
            ReturnDefaultIfFailed(result, textFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP));
        }
        else
        {
            //DWRITE_TRIMMING trimming = { DWRITE_TRIMMING_GRANULARITY_CHARACTER, '.', 3};
            //ReturnIfFailed(result, textFormat->SetTrimming(&trimming, nullptr));
            ReturnDefaultIfFailed(result, textFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP));
        }

        winrt::com_ptr<IDWriteTextLayout> textLayout;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextLayout(
            text.data(),					// The string to be laid out and formatted.
            text.size(),        			// The length of the string.
            textFormat.get(),			    // The text format to apply to the string (contains font information, etc).
            width,							// The width of the layout box.
            INFINITY,						// The height of the layout box.
            textLayout.put()				// The IDWriteTextLayout interface pointer.
        ));

        for (const TextEntity& entity : entities)
        {
            UINT32 startPosition = entity.Offset();
            UINT32 length = entity.Length();
            auto name = winrt::get_class_name(entity.Type());

            if (name == winrt::name_of<TextEntityTypeBold>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontWeight(DWRITE_FONT_WEIGHT_SEMI_BOLD, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeItalic>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontStyle(DWRITE_FONT_STYLE_ITALIC, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeStrikethrough>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetStrikethrough(TRUE, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeUnderline>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetUnderline(TRUE, { startPosition, length }));
            }
            //else if (name == winrt::name_of<TextEntityTypeCustomEmoji>())
            //{
            //    textLayout->SetInlineObject(m_customEmoji.get(), { startPosition, length });
            //}
            else if (name == winrt::name_of<TextEntityTypeCode>() || name == winrt::name_of<TextEntityTypePre>() || name == winrt::name_of<TextEntityTypePreCode>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontCollection(m_systemCollection.get(), { startPosition, length }));
                ReturnDefaultIfFailed(result, textLayout->SetFontFamilyName(L"Consolas", { startPosition, length }));
            }
        }

        DWRITE_TEXT_METRICS metrics;
        ReturnDefaultIfFailed(result, textLayout->GetMetrics(&metrics));

        UINT32 maxHitTestMetricsCount = metrics.lineCount * metrics.maxBidiReorderingDepth;
        UINT32 actualTestsCount;
        DWRITE_HIT_TEST_METRICS* ranges = new DWRITE_HIT_TEST_METRICS[maxHitTestMetricsCount];
        result = textLayout->HitTestTextRange(offset, length, 0, 0, ranges, maxHitTestMetricsCount, &actualTestsCount);

        if (result == E_NOT_SUFFICIENT_BUFFER)
        {
            delete[] ranges;

            ranges = new DWRITE_HIT_TEST_METRICS[actualTestsCount];
            result = textLayout->HitTestTextRange(offset, length, 0, 0, ranges, actualTestsCount, &actualTestsCount);
        }

        ReturnDefaultIfFailed(result, result);

        std::vector<Windows::Foundation::Rect> vector;

        for (int i = 0; i < actualTestsCount; i++)
        {
            float left = ranges[i].left;
            float top = ranges[i].top;
            float right = ranges[i].left + ranges[i].width;
            float bottom = ranges[i].top + ranges[i].height;

            vector.push_back({ left, top, right - left, bottom - top });
        }

        delete[] ranges;
        return winrt::single_threaded_vector<Windows::Foundation::Rect>(std::move(vector));
    }

    Windows::Foundation::Rect PlaceholderImageHelper::LayoutMetrics(hstring text, int32_t offset, int32_t length, IVector<TextEntity> entities, double fontSize, double width, bool rtl)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        //ReturnIfFailed(result, CreateTextFormat(fontSize));
        //ReturnIfFailed(result, m_appleFormat->SetReadingDirection(rtl ? DWRITE_READING_DIRECTION_RIGHT_TO_LEFT : DWRITE_READING_DIRECTION_LEFT_TO_RIGHT));

        winrt::com_ptr<IDWriteTextFormat> textFormat;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextFormat(
            L"Segoe UI Emoji",						// font family name
            m_fontCollection.get(),			        // system font collection
            DWRITE_FONT_WEIGHT_NORMAL,				// font weight 
            DWRITE_FONT_STYLE_NORMAL,				// font style
            DWRITE_FONT_STRETCH_NORMAL,				// default font stretch
            fontSize,								// font size
            L"",									// locale name
            textFormat.put()
        ));
        ReturnDefaultIfFailed(result, textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING));
        ReturnDefaultIfFailed(result, textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR));
        ReturnDefaultIfFailed(result, textFormat->SetReadingDirection(rtl ? DWRITE_READING_DIRECTION_RIGHT_TO_LEFT : DWRITE_READING_DIRECTION_LEFT_TO_RIGHT));
        //ReturnDefaultIfFailed(result, textFormat->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING_NO_WRAP));

        winrt::com_ptr<IDWriteTextLayout> textLayout;
        ReturnDefaultIfFailed(result, m_dwriteFactory->CreateTextLayout(
            text.data(),					// The string to be laid out and formatted.
            text.size(),        			// The length of the string.
            textFormat.get(),			    // The text format to apply to the string (contains font information, etc).
            width,							// The width of the layout box.
            INFINITY,						// The height of the layout box.
            textLayout.put()				// The IDWriteTextLayout interface pointer.
        ));

        for (const TextEntity& entity : entities)
        {
            UINT32 startPosition = entity.Offset();
            UINT32 length = entity.Length();
            auto name = winrt::get_class_name(entity.Type());

            if (name == winrt::name_of<TextEntityTypeBold>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontWeight(DWRITE_FONT_WEIGHT_SEMI_BOLD, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeItalic>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontStyle(DWRITE_FONT_STYLE_ITALIC, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeStrikethrough>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetStrikethrough(TRUE, { startPosition, length }));
            }
            else if (name == winrt::name_of<TextEntityTypeUnderline>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetUnderline(TRUE, { startPosition, length }));
            }
            //else if (name == winrt::name_of<TextEntityTypeCustomEmoji>())
            //{
            //    textLayout->SetInlineObject(m_customEmoji.get(), { startPosition, length });
            //}
            else if (name == winrt::name_of<TextEntityTypeCode>() || name == winrt::name_of<TextEntityTypePre>() || name == winrt::name_of<TextEntityTypePreCode>())
            {
                ReturnDefaultIfFailed(result, textLayout->SetFontCollection(m_systemCollection.get(), { startPosition, length }));
                ReturnDefaultIfFailed(result, textLayout->SetFontFamilyName(L"Consolas", { startPosition, length }));
            }
        }

        DWRITE_TEXT_METRICS metrics;
        ReturnDefaultIfFailed(result, textLayout->GetMetrics(&metrics));

        return { metrics.left, metrics.top, metrics.width, metrics.height };
    }

    HRESULT PlaceholderImageHelper::WriteBytes(IVector<byte> hash, IRandomAccessStream randomAccessStream) noexcept
    {
        HRESULT result;
        winrt::com_ptr<IStream> stream;
        ReturnIfFailed(result, CreateStreamOverRandomAccessStream(winrt::get_unknown(randomAccessStream), IID_PPV_ARGS(&stream)));

        auto yolo = std::vector<byte>(hash.begin(), hash.end());

        ReturnIfFailed(result, stream->Write(yolo.data(), hash.Size(), nullptr));
        ReturnIfFailed(result, stream->Seek({ 0 }, STREAM_SEEK_SET, nullptr));

        return S_OK;
    }

    CompositionPath PlaceholderImageHelper::GetTail(float width, float height, float topLeftRadius, float topRightRadius, float bottomRightRadius, float bottomLeftRadius)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink;
        winrt::com_ptr<ID2D1PathGeometry1> d2dPathGeometry;

        ReturnNullIfFailed(result, m_d2dFactory->CreatePathGeometry(d2dPathGeometry.put()));
        ReturnNullIfFailed(result, d2dPathGeometry->Open(d2dGeometrySink.put()));

        d2dGeometrySink->BeginFigure({ topLeftRadius, 0 }, D2D1_FIGURE_BEGIN_FILLED);

        // Top edge
        d2dGeometrySink->AddLine({ width - topRightRadius, 0 });

        // Top-right corner
        if (topRightRadius > 0)
            d2dGeometrySink->AddArc({ {width, topRightRadius}, {topRightRadius, topRightRadius}, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        // Right edge
        d2dGeometrySink->AddLine({ width, height - (bottomRightRadius > 0 ? bottomRightRadius : 15) });

        auto xshift = width - 30;
        auto yshift = height - 30;

        // Bottom-right corner
        if (bottomRightRadius > 0)
        {
            d2dGeometrySink->AddArc({ { width - bottomRightRadius, height }, { bottomRightRadius, bottomRightRadius}, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });
        }
        else
        {
            d2dGeometrySink->AddBezier({ { xshift + 30.f, yshift + 15.f }, { xshift + 30.f, yshift + 18.493f }, { xshift + 28.796f, yshift + 21.704f } });
            d2dGeometrySink->AddBezier({ { xshift + 26.802f, yshift + 24.259f }, { xshift + 26.802f, yshift + 27.222f }, { xshift + 29.444f, yshift + 28.889f } });
            d2dGeometrySink->AddBezier({ { xshift + 29.833f, yshift + 29.167f }, { xshift + 30.f, yshift + 29.444f }, { xshift + 29.815f, yshift + 29.815f } });
            d2dGeometrySink->AddBezier({ { xshift + 29.444f, yshift + 29.815f }, { xshift + 25.463f, yshift + 29.815f }, { xshift + 24.630f, yshift + 29.815f } });
            d2dGeometrySink->AddBezier({ { xshift + 21.667f, yshift + 28.444f }, { xshift + 19.630f, yshift + 29.444f }, { xshift + 17.407f, yshift + 30.f } });
        }

        // Bottom edge
        d2dGeometrySink->AddLine({ bottomLeftRadius > 0 ? bottomLeftRadius : 15, height });

        // Bottom-left corner
        if (bottomLeftRadius > 0)
        {
            d2dGeometrySink->AddArc({ { 0, height - bottomLeftRadius }, { bottomLeftRadius, bottomLeftRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });
        }
        else
        {
            d2dGeometrySink->AddBezier({ { 12.593f, yshift + 30.f }, { 10.370f, yshift + 29.444f }, { 8.333f, yshift + 28.444f } });
            d2dGeometrySink->AddBezier({ { 5.370f, yshift + 29.815f }, { 4.537f, yshift + 29.815f }, { 0.556f, yshift + 29.815f } });
            d2dGeometrySink->AddBezier({ { 0.185f, yshift + 29.815f }, { 0.f, yshift + 29.444f }, { 0.167f, yshift + 29.167f } });
            d2dGeometrySink->AddBezier({ { 0.556f, yshift + 28.889f }, { 3.198f, yshift + 27.222f }, { 3.198f, yshift + 24.259f } });
            d2dGeometrySink->AddBezier({ { 1.204f, yshift + 21.704f }, { 0.f, yshift + 18.493f }, { 0.f, yshift + 15.f } });
        }

        // Left edge
        d2dGeometrySink->AddLine({ 0, topLeftRadius });

        // Top-left corner
        if (topLeftRadius > 0)
            d2dGeometrySink->AddArc({ { topLeftRadius, 0 }, { topLeftRadius, topLeftRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);

        ReturnNullIfFailed(result, d2dGeometrySink->Close());

        auto geometry = winrt::make_self<CompositionPathSource>(d2dPathGeometry);
        return CompositionPath(geometry.as<winrt::Windows::Graphics::IGeometrySource2D>());
    }

    CompositionPath PlaceholderImageHelper::GetOutline(IVector<ClosedVectorPath> contours)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink;
        winrt::com_ptr<ID2D1PathGeometry1> d2dPathGeometry;

        ReturnNullIfFailed(result, m_d2dFactory->CreatePathGeometry(d2dPathGeometry.put()));
        ReturnNullIfFailed(result, d2dPathGeometry->Open(d2dGeometrySink.put()));

        for (const ClosedVectorPath& path : contours)
        {
            bool open = true;
            VectorPathCommandCubicBezierCurve endCurve{ nullptr };

            for (const VectorPathCommand& command : path.Commands())
            {
                if (auto line = command.try_as<VectorPathCommandLine>())
                {
                    auto endPoint = line.EndPoint();
                    if (open)
                    {
                        open = false;
                        d2dGeometrySink->BeginFigure({ (float)endPoint.X(), (float)endPoint.Y() }, D2D1_FIGURE_BEGIN_FILLED);
                    }
                    else
                    {
                        d2dGeometrySink->AddLine({ (float)endPoint.X(), (float)endPoint.Y() });
                    }
                }
                else if (auto cubicBezierCurve = command.try_as<VectorPathCommandCubicBezierCurve>())
                {
                    auto endPoint = cubicBezierCurve.EndPoint();

                    if (open)
                    {
                        open = false;
                        d2dGeometrySink->BeginFigure({ (float)endPoint.X(), (float)endPoint.Y() }, D2D1_FIGURE_BEGIN_FILLED);
                        endCurve = cubicBezierCurve;
                    }
                    else
                    {
                        auto controlPoint1 = cubicBezierCurve.StartControlPoint();
                        auto controlPoint2 = cubicBezierCurve.EndControlPoint();

                        d2dGeometrySink->AddBezier({
                            { (float)controlPoint1.X(), (float)controlPoint1.Y() },
                            { (float)controlPoint2.X(), (float)controlPoint2.Y() },
                            { (float)endPoint.X(), (float)endPoint.Y() }
                            });
                    }
                }
            }

            if (endCurve)
            {
                auto endPoint = endCurve.EndPoint();
                auto controlPoint1 = endCurve.StartControlPoint();
                auto controlPoint2 = endCurve.EndControlPoint();

                d2dGeometrySink->AddBezier({
                    { (float)controlPoint1.X(), (float)controlPoint1.Y() },
                    { (float)controlPoint2.X(), (float)controlPoint2.Y() },
                    { (float)endPoint.X(), (float)endPoint.Y() }
                    });
            }

            d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);
        }

        ReturnNullIfFailed(result, d2dGeometrySink->Close());

        auto geometry = winrt::make_self<CompositionPathSource>(d2dPathGeometry);
        return CompositionPath(geometry.as<winrt::Windows::Graphics::IGeometrySource2D>());
    }

    CompositionPath PlaceholderImageHelper::GetEllipticalClip(float width, float height, float radius, float x, float y)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink;
        winrt::com_ptr<ID2D1PathGeometry1> d2dPathGeometry;

        ReturnNullIfFailed(result, m_d2dFactory->CreatePathGeometry(d2dPathGeometry.put()));
        ReturnNullIfFailed(result, d2dPathGeometry->Open(d2dGeometrySink.put()));

        d2dGeometrySink->SetFillMode(D2D1_FILL_MODE_ALTERNATE);
        d2dGeometrySink->BeginFigure({ 0, 0 }, D2D1_FIGURE_BEGIN_FILLED);
        d2dGeometrySink->AddLine({ width, 0 });
        d2dGeometrySink->AddLine({ width, height });
        d2dGeometrySink->AddLine({ 0, height });
        d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);

        D2D1_POINT_2F startPoint = D2D1::Point2F(x + radius, y);
        D2D1_SIZE_F radii = D2D1::SizeF(radius, radius);

        d2dGeometrySink->BeginFigure(startPoint, D2D1_FIGURE_BEGIN_FILLED);
        d2dGeometrySink->AddArc(D2D1::ArcSegment(
            D2D1::Point2F(x - radius, y),
            radii,
            0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE,
            D2D1_ARC_SIZE_SMALL
        ));
        d2dGeometrySink->AddArc(D2D1::ArcSegment(
            startPoint,
            radii,
            0.0f,
            D2D1_SWEEP_DIRECTION_CLOCKWISE,
            D2D1_ARC_SIZE_SMALL
        ));
        d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);

        ReturnNullIfFailed(result, d2dGeometrySink->Close());

        auto geometry = winrt::make_self<CompositionPathSource>(d2dPathGeometry);
        return CompositionPath(geometry.as<winrt::Windows::Graphics::IGeometrySource2D>());
    }

    inline void AppendButton(winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink, float x, float y, float width, float height, float topLeftRadius, float topRightRadius, float bottomRightRadius, float bottomLeftRadius)
    {
        d2dGeometrySink->BeginFigure({ x + topLeftRadius, y }, D2D1_FIGURE_BEGIN_FILLED);

        // Top edge
        d2dGeometrySink->AddLine({ x + width - topRightRadius, y });

        // Top-right corner
        if (topRightRadius > 0)
            d2dGeometrySink->AddArc({ { x + width, y + topRightRadius }, { topRightRadius, topRightRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        // Right edge
        d2dGeometrySink->AddLine({ x + width, y + height - bottomRightRadius });

        // Bottom-right corner
        if (bottomRightRadius > 0)
            d2dGeometrySink->AddArc({ { x + width - bottomRightRadius, y + height }, { bottomRightRadius, bottomRightRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        // Bottom edge
        d2dGeometrySink->AddLine({ x + bottomLeftRadius, y + height });

        // Bottom-left corner
        if (bottomLeftRadius > 0)
            d2dGeometrySink->AddArc({ { x, y + height - bottomLeftRadius }, { bottomLeftRadius, bottomLeftRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        // Left edge
        d2dGeometrySink->AddLine({ x, y + topLeftRadius });

        // Top-left corner
        if (topLeftRadius > 0)
            d2dGeometrySink->AddArc({ { x + topLeftRadius, y }, { topLeftRadius, topLeftRadius }, 0, D2D1_SWEEP_DIRECTION_CLOCKWISE, D2D1_ARC_SIZE_SMALL });

        d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);
    }

    CompositionPath PlaceholderImageHelper::GetReplyMarkupClip(IVector<IVector<Windows::Foundation::Rect>> rows, float bottomRightRadius, float bottomLeftRadius)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink;
        winrt::com_ptr<ID2D1PathGeometry1> d2dPathGeometry;

        ReturnNullIfFailed(result, m_d2dFactory->CreatePathGeometry(d2dPathGeometry.put()));
        ReturnNullIfFailed(result, d2dPathGeometry->Open(d2dGeometrySink.put()));

        auto padding = 2;
        auto x = 0.f;
        auto y = 0.f;

        auto j = 0;

        for (const IVector<Windows::Foundation::Rect>& row : rows)
        {
            auto i = 0;

            for (const Windows::Foundation::Rect& button : row)
            {
                auto bottomRight = 4.f;
                auto bottomLeft = 4.f;

                if (j == rows.Size() - 1)
                {
                    if (i == 0)
                    {
                        bottomLeft = bottomLeftRadius;
                    }

                    if (i == row.Size() - 1)
                    {
                        bottomRight = bottomRightRadius;
                    }
                }

                AppendButton(d2dGeometrySink, button.X, button.Y, button.Width, button.Height, 4, 4, bottomRight, bottomLeft);

                i++;
            }

            j++;
        }

        ReturnNullIfFailed(result, d2dGeometrySink->Close());

        auto geometry = winrt::make_self<CompositionPathSource>(d2dPathGeometry);
        return CompositionPath(geometry.as<winrt::Windows::Graphics::IGeometrySource2D>());
    }

    CompositionPath PlaceholderImageHelper::GetVoiceNoteClip(IVector<byte> waveform, double waveformWidth)
    {
        std::lock_guard const guard(m_criticalSection);
        HRESULT result;

        winrt::com_ptr<ID2D1GeometrySink> d2dGeometrySink;
        winrt::com_ptr<ID2D1PathGeometry1> d2dPathGeometry;

        ReturnNullIfFailed(result, m_d2dFactory->CreatePathGeometry(d2dPathGeometry.put()));
        ReturnNullIfFailed(result, d2dPathGeometry->Open(d2dGeometrySink.put()));

        auto lines = waveform.Size() * 8 / 5;
        auto bytes = new double[lines];

        for (int i = 0; i < lines; i++)
        {
            int j = (i * 5) / 8, shift = (i * 5) % 8;
            bytes[i] = ((waveform.GetAt(j) | ((j + 1 < waveform.Size() ? waveform.GetAt(j + 1) : 0) << 8)) >> shift & 0x1F) / 31.0;
        }

        auto imageWidth = waveformWidth; // 142d; // double.IsNaN(ActualWidth) ? 142 : ActualWidth;
        auto imageHeight = 20;

        auto space = 1.0;
        auto lineWidth = 2.0;
        auto maxLines = (imageWidth - space) / (lineWidth + space);
        auto maxWidth = lines / maxLines;

        for (int index = 0; index < maxLines; index++)
        {
            auto lineIndex = (int)(index * maxWidth);
            auto lineHeight = bytes[lineIndex] * (double)(imageHeight - 2.0) + 2.0;

            float x1 = (int)(index * (lineWidth + space));
            float y1 = (imageHeight - (int)lineHeight) / 2;
            float x2 = (int)(index * (lineWidth + space) + lineWidth);
            float y2 = imageHeight - y1;

            //d2dGeometrySink->BeginFigure({ x1, y1 }, D2D1_FIGURE_BEGIN_FILLED);
            //d2dGeometrySink->AddLine({ x2, y1 });
            //d2dGeometrySink->AddLine({ x2, y2 });
            //d2dGeometrySink->AddLine({ x1, y2 });
            //d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);

            if (lineHeight > 2)
            {
                d2dGeometrySink->BeginFigure({ x1, y1 + 1 }, D2D1_FIGURE_BEGIN_FILLED);
                d2dGeometrySink->AddArc(D2D1::ArcSegment(
                    D2D1::Point2F(x2, y1 + 1),
                    D2D1::SizeF(1, 1),
                    0.0f,
                    D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    D2D1_ARC_SIZE_SMALL
                ));
                d2dGeometrySink->AddLine({ x2, y2 - 1 });
                d2dGeometrySink->AddArc(D2D1::ArcSegment(
                    { x1, y2 - 1 },
                    D2D1::SizeF(1, 1),
                    0.0f,
                    D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    D2D1_ARC_SIZE_SMALL
                ));
            }
            else
            {
                d2dGeometrySink->BeginFigure({ x1, 10 }, D2D1_FIGURE_BEGIN_FILLED);
                d2dGeometrySink->AddArc(D2D1::ArcSegment(
                    D2D1::Point2F(x2, 10),
                    D2D1::SizeF(1, 1),
                    0.0f,
                    D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    D2D1_ARC_SIZE_SMALL
                ));
                d2dGeometrySink->AddArc(D2D1::ArcSegment(
                    { x1, 10 },
                    D2D1::SizeF(1, 1),
                    0.0f,
                    D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    D2D1_ARC_SIZE_SMALL
                ));
            }

            d2dGeometrySink->EndFigure(D2D1_FIGURE_END_CLOSED);
        }

        delete[] bytes;

        ReturnNullIfFailed(result, d2dGeometrySink->Close());

        auto geometry = winrt::make_self<CompositionPathSource>(d2dPathGeometry);
        return CompositionPath(geometry.as<winrt::Windows::Graphics::IGeometrySource2D>());
    }

    HRESULT PlaceholderImageHelper::Encode(IBuffer source, IRandomAccessStream destination, int32_t width, int32_t height, int32_t rotation)
    {
        HRESULT result;
        winrt::com_ptr<IStream> stream;
        ReturnIfFailed(result, CreateStreamOverRandomAccessStream(winrt::get_unknown(destination), IID_PPV_ARGS(&stream)));

        if (destination.Size())
        {
            stream->SetSize({ 0 });
        }

        winrt::com_ptr<IWICBitmapEncoder> wicBitmapEncoder;
        ReturnIfFailed(result, m_wicFactory->CreateEncoder(GUID_ContainerFormatPng, nullptr, wicBitmapEncoder.put()));
        ReturnIfFailed(result, wicBitmapEncoder->Initialize(stream.get(), WICBitmapEncoderNoCache));

        winrt::com_ptr<IWICBitmapFrameEncode> wicFrameEncode;
        ReturnIfFailed(result, wicBitmapEncoder->CreateNewFrame(wicFrameEncode.put(), nullptr));
        ReturnIfFailed(result, wicFrameEncode->Initialize(nullptr));

        WICPixelFormatGUID pixelFormat = GUID_WICPixelFormat32bppBGRA;
        ReturnIfFailed(result, wicFrameEncode->SetSize(width, height));
        ReturnIfFailed(result, wicFrameEncode->SetPixelFormat(&pixelFormat));

        if (rotation)
        {
            winrt::com_ptr<IWICMetadataQueryWriter> pMetadataWriter;
            ReturnIfFailed(result, wicFrameEncode->GetMetadataQueryWriter(pMetadataWriter.put()));

            PROPVARIANT propValue;
            PropVariantInit(&propValue);
            propValue.vt = VT_UI2;

            switch (rotation)
            {
            case 90:
                propValue.uiVal = PHOTO_ORIENTATION_ROTATE270;
                break;
            case 180:
                propValue.uiVal = PHOTO_ORIENTATION_ROTATE180;
                break;
            case 270:
                propValue.uiVal = PHOTO_ORIENTATION_ROTATE90;
                break;
            default:
                propValue.uiVal = PHOTO_ORIENTATION_NORMAL;
                break;
            }

            ReturnIfFailed(result, pMetadataWriter->SetMetadataByName(L"System.Photo.Orientation", &propValue));
            PropVariantClear(&propValue);
        }

        ReturnIfFailed(result, wicFrameEncode->WritePixels(height, width * 4, width * height * 4, source.data()));
        ReturnIfFailed(result, wicFrameEncode->Commit());
        ReturnIfFailed(result, wicBitmapEncoder->Commit());

        ReturnIfFailed(result, stream->Commit(STGC_DEFAULT));

        return stream->Seek({ 0 }, STREAM_SEEK_SET, nullptr);
    }

    HRESULT PlaceholderImageHelper::SaveImageToStream(ID2D1Image* image, REFGUID wicFormat, IRandomAccessStream randomAccessStream)
    {
        HRESULT result;
        winrt::com_ptr<IStream> stream;
        ReturnIfFailed(result, CreateStreamOverRandomAccessStream(winrt::get_unknown(randomAccessStream), IID_PPV_ARGS(&stream)));

        if (randomAccessStream.Size())
        {
            stream->SetSize({ 0 });
        }

        winrt::com_ptr<IWICBitmapEncoder> wicBitmapEncoder;
        ReturnIfFailed(result, m_wicFactory->CreateEncoder(wicFormat, nullptr, wicBitmapEncoder.put()));
        ReturnIfFailed(result, wicBitmapEncoder->Initialize(stream.get(), WICBitmapEncoderNoCache));

        winrt::com_ptr<IWICBitmapFrameEncode> wicFrameEncode;
        ReturnIfFailed(result, wicBitmapEncoder->CreateNewFrame(wicFrameEncode.put(), nullptr));
        ReturnIfFailed(result, wicFrameEncode->Initialize(nullptr));

        ReturnIfFailed(result, m_imageEncoder->WriteFrame(image, wicFrameEncode.get(), nullptr));
        ReturnIfFailed(result, wicFrameEncode->Commit());
        ReturnIfFailed(result, wicBitmapEncoder->Commit());

        ReturnIfFailed(result, stream->Commit(STGC_DEFAULT));

        return stream->Seek({ 0 }, STREAM_SEEK_SET, nullptr);
    }
} // namespace winrt::Telegram::Native::implementation