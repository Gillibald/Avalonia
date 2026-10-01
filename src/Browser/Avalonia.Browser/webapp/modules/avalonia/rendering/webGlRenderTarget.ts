import { BrowserRenderingMode } from "./renderingMode";
import { WebRenderTarget } from "./webRenderTarget";
interface EmscriptenGlContext {
    handle: number;
}

interface EmscriptenGL {
    registerContext: (ctx: WebGLRenderingContext, attrs: WebGLContextAttributes) => number;
    currentContext?: EmscriptenGlContext;
    makeContextCurrent: (handle: number) => boolean;
}

function getGL(): EmscriptenGL {
    const self = globalThis as any;
    const module = self.Module ?? self.getDotnetRuntime(0)?.Module;
    return (module?.GL ?? self.AvaloniaGL ?? self.SkiaSharpGL) as EmscriptenGL;
}

export class WebGlRenderTarget extends WebRenderTarget {
    public contextHandle?: number;
    public attrs: WebGLContextAttributes;
    public fboId?: number;
    public stencil?: number;
    public sample?: number;
    public depth?: number;
    public renderer?: string;
    private static _gl: EmscriptenGL | null = null;

    constructor(public canvas: HTMLCanvasElement | OffscreenCanvas, mode: BrowserRenderingMode) {
        // Skia only understands WebGL context wrapped in Emscripten.
        if (WebGlRenderTarget._gl == null) { WebGlRenderTarget._gl = getGL(); }
        if (!WebGlRenderTarget._gl) {
            throw new Error("Module.GL object wasn't initialized, WebGL can't be used.");
        }

        const attrs: WebGLContextAttributes | any =
            {
                alpha: true,
                depth: true,
                stencil: true,
                antialias: false,
                premultipliedAlpha: true,
                preserveDrawingBuffer: false,
                // only supported on older browsers, which is perfect as we want to fallback to 2d there.
                failIfMajorPerformanceCaveat: true,
                // attrs used by Emscripten:
                majorVersion: mode === BrowserRenderingMode.WebGL1 ? 1 : 2,
                minorVersion: 0,
                enableExtensionsByDefault: 1,
                explicitSwapControl: 0
            };

        const context = (mode === BrowserRenderingMode.WebGL1
            ? canvas.getContext("webgl", attrs)
            : canvas.getContext("webgl2", attrs)) as WebGLRenderingContext;
        if (!context) {
            throw new Error("HTMLCanvasElement.getContext returned null.");
        }

        const handle = WebGlRenderTarget._gl.registerContext(context, attrs);
        (context as any).gl_handle = handle;
        super(canvas, "webgl");

        this.contextHandle = handle;
        this.fboId = context.getParameter(context.FRAMEBUFFER_BINDING)?.id ?? 0;
        this.stencil = context.getParameter(context.STENCIL_BITS);
        this.sample = context.getParameter(context.SAMPLES);
        this.depth = context.getParameter(context.DEPTH_BITS);
        this.renderer = WebGlRenderTarget.getRendererName(context);
        this.attrs = attrs;
    }

    // Chromium and WebKit report "WebKit WebGL" as RENDERER, which is all GL_RENDERER shows Skia; the unmasked name
    // tells a software implementation such as SwiftShader from a GPU. Firefox reports the real (sanitized) renderer as
    // RENDERER and flags the debug extension as deprecated, so the extension is only asked where RENDERER is masked.
    private static getRendererName(context: WebGLRenderingContext): string | undefined {
        try {
            const renderer = context.getParameter(context.RENDERER);

            if (typeof renderer === "string" && renderer !== "WebKit WebGL") {
                return renderer;
            }

            const info = context.getExtension("WEBGL_debug_renderer_info");
            const unmasked = info ? context.getParameter(info.UNMASKED_RENDERER_WEBGL) : null;

            return typeof unmasked === "string" ? unmasked : (renderer ?? undefined);
        } catch {
            return undefined;
        }
    }

    public static getCurrentContext(): number {
        return WebGlRenderTarget._gl?.currentContext?.handle ?? 0;
    }

    public static makeContextCurrent(handle: number): boolean {
        if (WebGlRenderTarget._gl == null) { return false; }
        const ret = WebGlRenderTarget._gl.makeContextCurrent(handle);
        return handle === 0 || ret;
    }
}
