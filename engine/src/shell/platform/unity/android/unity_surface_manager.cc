#include "unity_surface_manager.h"

#include <flutter/fml/logging.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES2/gl2.h>
#include <GLES2/gl2ext.h>

#include <vulkan/vulkan.h>
#include <vulkan/vulkan_core.h>
#include <android/hardware_buffer.h>

#include "src/shell/common/shell_io_manager.h"
#include "src/shell/gpu/gpu_surface_delegate.h"
#include "src/shell/gpu/gpu_surface_gl_delegate.h"

#define UNITY_USED_VULKAN_API_FUNCTIONS(apply) \
  apply(vkGetDeviceProcAddr);                  \
  apply(vkCreateInstance);                     \
  apply(vkCmdBeginRenderPass);                 \
  apply(vkCreateBuffer);                       \
  apply(vkGetPhysicalDeviceMemoryProperties);  \
  apply(vkGetBufferMemoryRequirements);        \
  apply(vkMapMemory);                          \
  apply(vkBindBufferMemory);                   \
  apply(vkAllocateMemory);                     \
  apply(vkDestroyBuffer);                      \
  apply(vkFreeMemory);                         \
  apply(vkUnmapMemory);                        \
  apply(vkQueueWaitIdle);                      \
  apply(vkDeviceWaitIdle);                     \
  apply(vkCmdCopyBufferToImage);               \
  apply(vkFlushMappedMemoryRanges);            \
  apply(vkCreatePipelineLayout);               \
  apply(vkCreateShaderModule);                 \
  apply(vkDestroyShaderModule);                \
  apply(vkCreateGraphicsPipelines);            \
  apply(vkCmdBindPipeline);                    \
  apply(vkCmdDraw);                            \
  apply(vkCmdPushConstants);                   \
  apply(vkCmdBindVertexBuffers);               \
  apply(vkDestroyPipeline);                    \
  apply(vkDestroyPipelineLayout);

#define VULKAN_DEFINE_API_FUNCPTR(func) static PFN_##func my_##func
VULKAN_DEFINE_API_FUNCPTR(vkGetInstanceProcAddr);
UNITY_USED_VULKAN_API_FUNCTIONS(VULKAN_DEFINE_API_FUNCPTR);
#undef VULKAN_DEFINE_API_FUNCPTR

static void LoadVulkanAPI(PFN_vkGetInstanceProcAddr getInstanceProcAddr,
                          VkInstance instance)
{
  if (!my_vkGetInstanceProcAddr && getInstanceProcAddr)
    my_vkGetInstanceProcAddr = getInstanceProcAddr;

  if (!my_vkCreateInstance)
    my_vkCreateInstance = (PFN_vkCreateInstance)my_vkGetInstanceProcAddr(
        VK_NULL_HANDLE, "vkCreateInstance");

#define LOAD_VULKAN_FUNC(fn) \
  if (!my_##fn)              \
  my_##fn = (PFN_##fn)my_vkGetInstanceProcAddr(instance, #fn)
  UNITY_USED_VULKAN_API_FUNCTIONS(LOAD_VULKAN_FUNC);
#undef LOAD_VULKAN_FUNC
}

namespace uiwidgets
{

  static EGLDisplay egl_display_;
  static EGLContext egl_unity_context_;

  template <class T>
  using EGLResult = std::pair<bool, T>;

  UnitySurfaceManager::UnitySurfaceManager(IUnityInterfaces *unity_interfaces)
      : egl_context_(EGL_NO_CONTEXT),
        egl_resource_context_(EGL_NO_CONTEXT)
  {
    initialize_succeeded_ = Initialize(unity_interfaces);
  }

  UnitySurfaceManager::~UnitySurfaceManager() { CleanUp(); }

  GLuint UnitySurfaceManager::CreateRenderSurface(void *native_texture_ptr)
  {
    if (m_UnityVulkan != nullptr)
    {
      UnityVulkanImage image;
      m_UnityVulkan->AccessTexture(
          native_texture_ptr, UnityVulkanWholeImage, VkImageLayout::VK_IMAGE_LAYOUT_UNDEFINED, 0,
          0, UnityVulkanResourceAccessMode::kUnityVulkanResourceAccess_ObserveOnly,
          &image);

      GrVkImageInfo info(
          image.image, GrVkAlloc(image.memory.memory, image.memory.offset, image.memory.size, image.memory.flags),
          image.tiling,
          image.layout,
          image.format,
          image.mipCount);

      int width = 100;
      int height = 100;
      GrBackendTexture backendTex(width, height, info);

      m_SkSurface = SkSurface::MakeFromBackendTexture(
          gr_context_.get(), backendTex, kBottomLeft_GrSurfaceOrigin, 1,
          kRGBA_8888_SkColorType, nullptr, nullptr);
      return 0;
    }
    else
    {
      GLint old_framebuffer_binding;
      glGetIntegerv(GL_FRAMEBUFFER_BINDING, &old_framebuffer_binding);

      glGenFramebuffers(1, &fbo_);
      glBindFramebuffer(GL_FRAMEBUFFER, fbo_);

      GLuint gltex = (GLuint)(size_t)(native_texture_ptr);
      glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, gltex, 0);
      FML_CHECK(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE);

      glBindFramebuffer(GL_FRAMEBUFFER, old_framebuffer_binding);

      return fbo_;
    }
  }

  void UnitySurfaceManager::DestroyRenderSurface()
  {
    FML_DCHECK(fbo_ != 0);
    glDeleteFramebuffers(1, &fbo_);
    fbo_ = 0;
  }

  bool UnitySurfaceManager::ClearCurrent()
  {
    return eglMakeCurrent(egl_display_, EGL_NO_SURFACE, EGL_NO_SURFACE,
                          EGL_NO_CONTEXT) == EGL_TRUE;
  }

  bool UnitySurfaceManager::MakeCurrent(const EGLSurface surface)
  {
    return eglMakeCurrent(egl_display_, surface, surface, egl_context_) ==
           EGL_TRUE;
  }

  bool UnitySurfaceManager::MakeResourceCurrent()
  {
    return eglMakeCurrent(egl_display_, EGL_NO_SURFACE, EGL_NO_SURFACE,
                          egl_resource_context_) == EGL_TRUE;
  }

  static EGLResult<EGLConfig> ChooseEGLConfiguration(EGLDisplay display)
  {
    EGLint attributes[] = {
        // clang-format off
      EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
      EGL_SURFACE_TYPE,    EGL_WINDOW_BIT,
      EGL_RED_SIZE,        8,
      EGL_GREEN_SIZE,      8,
      EGL_BLUE_SIZE,       8,
      EGL_ALPHA_SIZE,      8,
      EGL_DEPTH_SIZE,      0,
      EGL_STENCIL_SIZE,    0,
      EGL_NONE,            // termination sentinel
        // clang-format on
    };

    EGLint config_count = 0;
    EGLConfig egl_config = nullptr;

    if (eglChooseConfig(display, attributes, &egl_config, 1, &config_count) !=
        EGL_TRUE)
    {
      return {false, nullptr};
    }

    bool success = config_count > 0 && egl_config != nullptr;

    return {success, success ? egl_config : nullptr};
  }

  static EGLResult<EGLSurface> CreateContext(EGLDisplay display,
                                             EGLConfig config,
                                             EGLContext share = EGL_NO_CONTEXT)
  {
    EGLint attributes[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};

    EGLContext context = eglCreateContext(display, config, share, attributes);

    return {context != EGL_NO_CONTEXT, context};
  }

  void UnitySurfaceManager::GetUnityContext()
  {
    if (egl_unity_context_ != nullptr)
    {
      return;
    }
    // EGLint major, minor;

    // egl_display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    // eglInitialize(egl_display_, &major, &minor);
    // egl_unity_context_ = eglGetCurrentContext();
    // FML_CHECK(egl_display_ != EGL_NO_DISPLAY)
    //     << "Renderer type is invalid";
  }

  static const int DEV_W = 16, DEV_H = 16;

  bool UnitySurfaceManager::Initialize(IUnityInterfaces *unity_interfaces)
  {
    auto *graphics = unity_interfaces->Get<IUnityGraphics>();
    UnityGfxRenderer renderer = graphics->GetRenderer();
    FML_DCHECK(renderer == kUnityGfxRendererOpenGLES30 ||
               renderer == kUnityGfxRendererOpenGLES20 ||
               renderer == kUnityGfxRendererVulkan);

    if (renderer == kUnityGfxRendererVulkan)
    {

      m_UnityVulkan = unity_interfaces->Get<IUnityGraphicsVulkan>();
      m_Instance = m_UnityVulkan->Instance();
      LoadVulkanAPI(m_Instance.getInstanceProcAddr, m_Instance.instance);

      // // AHarewareBuffer android api >= 26
      // AHardwareBuffer* buffer = nullptr;

      // AHardwareBuffer_Desc hwbDesc;
      // hwbDesc.width = DEV_W;
      // hwbDesc.height = DEV_H;
      // hwbDesc.layers = 1;

      // hwbDesc.usage = AHARDWAREBUFFER_USAGE_CPU_READ_NEVER |
      //                   AHARDWAREBUFFER_USAGE_CPU_WRITE_NEVER |
      //                   AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE |
      //                   AHARDWAREBUFFER_USAGE_GPU_COLOR_OUTPUT;
      // hwbDesc.format = AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM;
      // // The following three are not used in the allocate
      // hwbDesc.stride = 0;
      // hwbDesc.rfu0= 0;
      // hwbDesc.rfu1= 0;

      // if (int error = AHardwareBuffer_allocate(&hwbDesc, &buffer)) {
      //     ERRORF(reporter, "Failed to allocated hardware buffer, error: %d", error);
      //      AHardwareBuffer_release(buffer);
      //     return;
      // }

      auto device = m_Instance.device;
      auto physicalDevice = m_Instance.physicalDevice;
      VkImage image = 0;
      VkImageCreateInfo imageInfo{};
      imageInfo.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
      imageInfo.imageType = VK_IMAGE_TYPE_2D;
      imageInfo.extent.width = 100;
      imageInfo.extent.height = 100;
      imageInfo.extent.depth = 1;
      imageInfo.mipLevels = 1;
      imageInfo.arrayLayers = 1;
      imageInfo.format = VK_FORMAT_B8G8R8A8_UNORM;
      imageInfo.tiling = VK_IMAGE_TILING_LINEAR;
      imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
      imageInfo.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
      imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
      imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
      vkCreateImage(device, &imageInfo, nullptr, &image);
      // ... // create the image as normal
      VkMemoryRequirements memReqs; // = device->getImageMemoryRequirements(image);
      vkGetImageMemoryRequirements(device, image, &memReqs);
      VkMemoryAllocateInfo memAllocInfo;
      const auto handle_type = VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_FD_BIT_KHR;
      VkExportMemoryAllocateInfoKHR exportAllocInfo{
          VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO_KHR, nullptr, handle_type};
      memAllocInfo.pNext = &exportAllocInfo;
      memAllocInfo.allocationSize = memReqs.size;

      uint32_t memoryTypeIndex = 0;
      bool foundHeap = false;
      VkPhysicalDeviceMemoryProperties phyDevMemProps;
      vkGetPhysicalDeviceMemoryProperties(physicalDevice, &phyDevMemProps);
      for (uint32_t i = 0; i < phyDevMemProps.memoryTypeCount && !foundHeap; ++i)
      {
        if (VkMemoryPropertyFlagBits::VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT & (1 << i))
        {
          // Map host-visible memory.
          if (phyDevMemProps.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)
          {
            memoryTypeIndex = i;
            foundHeap = true;
          }
        }
      }

      memAllocInfo.memoryTypeIndex = memoryTypeIndex;
      // physicalDevice->set_memory_type(
      //   memReqs.memoryTypeBits, &memAllocInfo, VkMemoryPropertyFlagBits::VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
      VkDeviceMemory memory;
      vkAllocateMemory(device, &memAllocInfo, NULL, &memory);
      // memory = device->allocateMemory(memAllocInfo);
      vkBindImageMemory(device, image, memory, 0);

      AHardwareBuffer *buffer;

      VkMemoryGetAndroidHardwareBufferInfoANDROID meminfo;
      meminfo.sType = VK_STRUCTURE_TYPE_MEMORY_GET_ANDROID_HARDWARE_BUFFER_INFO_ANDROID;
      meminfo.pNext = nullptr;
      meminfo.memory = memory;
      vkGetMemoryAndroidHardwareBufferANDROID(device, &meminfo, &buffer); // android api level 28
      // HANDLE sharedMemoryHandle = device->getMemoryWin32HandleKHR({
      //   texture.memory, VkExternalMemoryHandleTypeFlags::VK_EXTERNAL_MEMORY_HANDLE_TYPE_ANDROID_HARDWARE_BUFFER_BIT_ANDROID
      // });
      // EGLDisplay display = eglGetCurrentDisplay();

      bool success = false;
      EGLint major, minor;

      egl_display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
      auto error = eglGetError();
      eglInitialize(egl_display_, &major, &minor);
      error = eglGetError();
      std::tie(success, egl_config_) = ChooseEGLConfiguration(egl_display_);
      error = eglGetError();
      FML_CHECK(success) << "Could not choose an EGL configuration.";
      eglBindAPI(EGL_OPENGL_ES_API);

      error = eglGetError();
      std::tie(success, egl_context_) = CreateContext(egl_display_, egl_config_, EGL_NO_CONTEXT);
      error = eglGetError();

      std::tie(success, egl_resource_context_) = CreateContext(egl_display_, egl_config_, egl_context_);
      eglMakeCurrent(egl_display_, EGL_NO_SURFACE, EGL_NO_SURFACE, egl_context_);
      error = eglGetError();
      GLuint mTexture = 0;
      glGenTextures(1, &mTexture);
      error = eglGetError();
      glGenFramebuffers(1, &fbo_);
      error = eglGetError();
      glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
      error = eglGetError();

      glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, mTexture, 0);
      error = eglGetError();
      
      EGLint ver0 = -1;
      EGLint ver1 = -1;
      EGLint ver2 = -1;
      EGLint ver3 = -1;

      eglQueryContext(egl_display_, egl_context_, EGL_CONTEXT_CLIENT_VERSION, &ver0);

      eglQueryContext(egl_display_, egl_context_, EGL_CONFIG_ID, &ver1);
      eglQueryContext(egl_display_, egl_context_, EGL_CONTEXT_CLIENT_TYPE, &ver2);
      eglQueryContext(egl_display_, egl_context_, EGL_RENDER_BUFFER, &ver3);
      AHardwareBuffer_Desc usage;

      // filling in the usage for HardwareBuffer
      usage.format = AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM;
      usage.height = 100; //outputHeight;
      usage.width = 100;  // outputWidth;
      usage.layers = 1;
      usage.rfu0 = 0;
      usage.rfu1 = 0;
      usage.stride = 10;
      usage.usage = AHARDWAREBUFFER_USAGE_CPU_READ_OFTEN | AHARDWAREBUFFER_USAGE_CPU_WRITE_NEVER | AHARDWAREBUFFER_USAGE_GPU_COLOR_OUTPUT;

      AHardwareBuffer *graphicBuf;
      AHardwareBuffer_allocate(&usage, &graphicBuf); // it's worth to check the return code

      // ACTUAL parameters of the AHardwareBuffer which it reports
      AHardwareBuffer_Desc usage1;

      // for stride, see below
      AHardwareBuffer_describe(graphicBuf, &usage1);

      // get the native buffer
      EGLClientBuffer clientBuf = eglGetNativeClientBufferANDROID(graphicBuf);
      error = eglGetError();

      // obtaining the EGL display

      // specifying the image attributes
      // EGLint eglImageAttributes[] = {EGL_IMAGE_PRESERVED_KHR, EGL_TRUE, EGL_NONE};
      EGLint eglImageAttributes[] = { EGL_NONE};

      // creating an EGL image

      // EGLImageKHR imageEGL = eglCreateImageKHR(egl_display_, egl_context_, EGL_NATIVE_BUFFER_ANDROID, clientBuf, eglImageAttributes);
      EGLImageKHR imageEGL = eglCreateImageKHR(egl_display_, EGL_NO_CONTEXT, EGL_NATIVE_BUFFER_ANDROID, clientBuf, eglImageAttributes);
      error = eglGetError();
      /**
       * @note this part should be earlies than any draw or framebuffer options.
       * @note refer to answer of @solidpixel at https://stackoverflow.com/questions/64447069/use-gleglimagetargettexture2does-to-replace-glreadpixels-on-android
       * @{
       */
      // binding the OUTPUT texture
      glBindTexture(GL_TEXTURE_2D, mTexture);
      error = eglGetError();

      glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, imageEGL);
      error = eglGetError();
      glBindTexture(bindType, 0);

      auto x = glCheckFramebufferStatus(GL_FRAMEBUFFER);
      //  glewInit();
      error = eglGetError();
      EGLClientBuffer clientBuffer = eglGetNativeClientBufferANDROID(buffer);
      error = eglGetError();
      bool isProtectedContent = true;
      EGLint attribs[] = {EGL_IMAGE_PRESERVED_KHR, EGL_TRUE, EGL_NONE};
      error = eglGetError();
      auto xegl = eglGetCurrentContext();
      // EGLImageKHR imagekhr = eglCreateImageKHR(egl_display_, egl_context_, EGL_NATIVE_BUFFER_ANDROID, clientBuffer, attribs);
       EGLImageKHR imagekhr = eglCreateImageKHR(egl_display_, EGL_NO_CONTEXT, EGL_NATIVE_BUFFER_ANDROID, clientBuffer, attribs);
      error = eglGetError();
      GLint old_framebuffer_binding;
      glGetIntegerv(GL_FRAMEBUFFER_BINDING, &old_framebuffer_binding);
      error = eglGetError();
      glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, imagekhr);
      error = eglGetError();
      glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, mTexture, 0);
      error = eglGetError();

      FML_CHECK(glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE);

      glBindFramebuffer(GL_FRAMEBUFFER, old_framebuffer_binding);

      //-------------------

      UnityVulkanPluginEventConfig config_1;
      config_1.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_DontCare;
      config_1.renderPassPrecondition = kUnityVulkanRenderPass_EnsureInside;
      config_1.flags =
          kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission |
          kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState;
      m_UnityVulkan->ConfigureEvent(1, &config_1);

      GrVkBackendContext vk_backend_context;
      vk_backend_context.fInstance = m_Instance.instance;
      vk_backend_context.fPhysicalDevice = m_Instance.physicalDevice;
      vk_backend_context.fDevice = m_Instance.device;
      vk_backend_context.fQueue = m_Instance.graphicsQueue;
      vk_backend_context.fGraphicsQueueIndex = m_Instance.queueFamilyIndex;
      vk_backend_context.fGetProc =
          [getInstanceProc = m_Instance.getInstanceProcAddr,
           getDeviceProc = my_vkGetDeviceProcAddr](
              const char *proc_name, VkInstance instance, VkDevice device) {
            if (device != VK_NULL_HANDLE)
            {
              return getDeviceProc(device, proc_name);
            }
            return getInstanceProc(instance, proc_name);
          };
      gr_context_ = GrContext::MakeVulkan(vk_backend_context);

      auto valid_ = true;

      return success;
    }
    else
    {
      // Make sure Vulkan API functions are loaded

      FML_CHECK(egl_display_ != EGL_NO_DISPLAY)
          << "Renderer type is invalid";

      // Initialize the display connection.
      FML_CHECK(eglInitialize(egl_display_, nullptr, nullptr) == EGL_TRUE)
          << "Renderer type is invalid";

      auto valid_ = true;

      bool success = false;

      std::tie(success, egl_config_) = ChooseEGLConfiguration(egl_display_);
      FML_CHECK(success) << "Could not choose an EGL configuration.";

      std::tie(success, egl_context_) = CreateContext(egl_display_, egl_config_, egl_unity_context_);

      std::tie(success, egl_resource_context_) = CreateContext(egl_display_, egl_config_, egl_context_);
      return success;
    }
  }

  void UnitySurfaceManager::CleanUp()
  {
    if (egl_display_ != EGL_NO_DISPLAY &&
        egl_resource_context_ != EGL_NO_CONTEXT)
    {
      eglDestroyContext(egl_display_, egl_resource_context_);
      egl_resource_context_ = EGL_NO_CONTEXT;
    }
    if (egl_display_ != EGL_NO_DISPLAY && egl_context_ != EGL_NO_CONTEXT)
    {
      eglDestroyContext(egl_display_, egl_context_);
      egl_context_ = EGL_NO_CONTEXT;
    }
  }

} // namespace uiwidgets
