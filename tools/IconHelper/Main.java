import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.Base64;

/**
 * Renders Android application icons and reads the active media session on the device.
 *
 * Run with: CLASSPATH=/data/local/tmp/scrcpylauncher-iconhelper.jar app_process / Main OUT_DIR SIZE PKG...
 * Or: CLASSPATH=/data/local/tmp/scrcpylauncher-iconhelper.jar app_process / Main media snapshot ART_FILE
 * Prints "OK pkg" or "FAIL pkg reason" for every package.
 *
 * Written against JDK classes only (Android framework APIs are reached via reflection)
 * so it can be compiled with a plain JDK and dexed with d8, without the Android SDK.
 */
public class Main {
    private static final int DENSITY = 480;

    public static void main(String[] args) throws Throwable {
        if (args.length > 0 && "media".equals(args[0])) {
            runMediaCommand(args);
            System.exit(0);
            return;
        }
        if (args.length < 3) {
            System.out.println("usage: Main OUT_DIR SIZE PKG...");
            return;
        }
        String outDir = args[0];
        int size = Integer.parseInt(args[1]);
        new File(outDir).mkdirs();

        try {
            Class.forName("android.os.Looper").getMethod("prepareMainLooper").invoke(null);
        } catch (Throwable ignored) {
        }

        Class<?> serviceManager = Class.forName("android.os.ServiceManager");
        Object binder = serviceManager.getMethod("getService", String.class).invoke(null, "package");
        Class<?> stub = Class.forName("android.content.pm.IPackageManager$Stub");
        Object ipm = stub.getMethod("asInterface", Class.forName("android.os.IBinder")).invoke(null, binder);

        Method getAppInfo = null;
        for (Method m : ipm.getClass().getMethods()) {
            Class<?>[] p = m.getParameterTypes();
            if (m.getName().equals("getApplicationInfo") && p.length == 3 && p[0] == String.class) {
                getAppInfo = m;
                break;
            }
        }
        if (getAppInfo == null) {
            System.out.println("FAIL * getApplicationInfo not found");
            return;
        }
        boolean longFlags = getAppInfo.getParameterTypes()[1] == long.class;

        for (int i = 2; i < args.length; i++) {
            String pkg = args[i];
            try {
                Object ai = getAppInfo.invoke(ipm, pkg, longFlags ? (Object) 0L : (Object) 0, 0);
                if (ai == null) {
                    System.out.println("FAIL " + pkg + " not-found");
                    continue;
                }
                render(ai, new File(outDir, pkg + ".png"), size);
                System.out.println("OK " + pkg);
            } catch (Throwable t) {
                Throwable c = t;
                while (c.getCause() != null) c = c.getCause();
                System.out.println("FAIL " + pkg + " " + c.getClass().getSimpleName() + ": " + c.getMessage());
            }
        }
        System.out.flush();
        System.exit(0);
    }

    private static void runMediaCommand(String[] args) throws Throwable {
        if (args.length != 3 || !"snapshot".equals(args[1])) {
            throw new IllegalArgumentException("usage: Main media snapshot ART_FILE");
        }
        writeMediaSnapshot(args[2]);
        System.out.flush();
    }

    private static ActiveMedia selectActiveMedia() throws Throwable {
        Class<?> serviceManager = Class.forName("android.os.ServiceManager");
        Object binder = serviceManager.getMethod("getService", String.class).invoke(null, "media_session");
        if (binder == null) throw new IllegalStateException("Media session service unavailable");

        Class<?> binderClass = Class.forName("android.os.IBinder");
        Class<?> stubClass = Class.forName("android.media.session.ISessionManager$Stub");
        Object manager = stubClass.getMethod("asInterface", binderClass).invoke(null, binder);
        Class<?> managerClass = Class.forName("android.media.session.ISessionManager");
        Object token = managerClass.getMethod("getMediaKeyEventSession", String.class)
            .invoke(manager, "com.android.shell");
        if (token == null) return null;

        Class<?> tokenClass = Class.forName("android.media.session.MediaSession$Token");
        Object controller = tokenClass.getMethod("getBinder").invoke(token);
        Class<?> controllerClass = Class.forName("android.media.session.ISessionController");
        String owner = (String) controllerClass.getMethod("getPackageName").invoke(controller);
        Object metadata = controllerClass.getMethod("getMetadata").invoke(controller);
        Object playbackState = controllerClass.getMethod("getPlaybackState").invoke(controller);
        return new ActiveMedia(controller, metadata, playbackState, owner);
    }

    private static void writeMediaSnapshot(String artPath) throws Throwable {
        File artFile = new File(artPath);
        if (artFile.exists()) artFile.delete();

        ActiveMedia media = selectActiveMedia();
        if (media == null) {
            System.out.println("NONE");
            return;
        }

        String title = metadataString(media.metadata, "android.media.metadata.DISPLAY_TITLE");
        if (title.length() == 0) title = metadataString(media.metadata, "android.media.metadata.TITLE");
        if (title.length() == 0) title = media.packageName;
        String artist = metadataString(media.metadata, "android.media.metadata.DISPLAY_SUBTITLE");
        if (artist.length() == 0) artist = metadataString(media.metadata, "android.media.metadata.ARTIST");
        String album = metadataString(media.metadata, "android.media.metadata.ALBUM");
        String mediaId = metadataString(media.metadata, "android.media.metadata.MEDIA_ID");

        long position = 0;
        long duration = metadataLong(media.metadata, "android.media.metadata.DURATION");
        long actions = 0;
        if (media.playbackState != null) {
            Class<?> stateClass = media.playbackState.getClass();
            position = ((Long) stateClass.getMethod("getPosition").invoke(media.playbackState)).longValue();
            actions = ((Long) stateClass.getMethod("getActions").invoke(media.playbackState)).longValue();
        }

        Object bitmap = metadataBitmap(media.metadata, "android.media.metadata.ART");
        if (bitmap == null) bitmap = metadataBitmap(media.metadata, "android.media.metadata.ALBUM_ART");
        if (bitmap == null) bitmap = metadataBitmap(media.metadata, "android.media.metadata.DISPLAY_ICON");
        boolean hasArt = bitmap != null && writeBitmap(bitmap, artFile);
        int state = media.playbackState == null
            ? 0
            : ((Integer) media.playbackState.getClass().getMethod("getState").invoke(media.playbackState)).intValue();

        System.out.println("MEDIA\t" + encode(media.packageName)
            + "\t" + encode(title)
            + "\t" + encode(artist)
            + "\t" + encode(album)
            + "\t" + state
            + "\t" + position
            + "\t" + duration
            + "\t" + actions
            + "\t" + encode(mediaId)
            + "\t" + (hasArt ? "1" : "0"));
    }

    private static String metadataString(Object metadata, String key) throws Throwable {
        if (metadata == null) return "";
        Object value = metadata.getClass().getMethod("getString", String.class).invoke(metadata, key);
        return value == null ? "" : value.toString();
    }

    private static long metadataLong(Object metadata, String key) throws Throwable {
        if (metadata == null) return 0;
        return ((Long) metadata.getClass().getMethod("getLong", String.class).invoke(metadata, key)).longValue();
    }

    private static Object metadataBitmap(Object metadata, String key) throws Throwable {
        if (metadata == null) return null;
        return metadata.getClass().getMethod("getBitmap", String.class).invoke(metadata, key);
    }

    private static boolean writeBitmap(Object bitmap, File output) throws Throwable {
        if (bitmap == null) return false;
        File parent = output.getParentFile();
        if (parent != null) parent.mkdirs();

        Class<?> bitmapClass = Class.forName("android.graphics.Bitmap");
        Class<?> formatClass = Class.forName("android.graphics.Bitmap$CompressFormat");
        @SuppressWarnings({"unchecked", "rawtypes"})
        Object png = Enum.valueOf((Class) formatClass, "PNG");
        try (FileOutputStream stream = new FileOutputStream(output)) {
            return ((Boolean) bitmapClass.getMethod("compress", formatClass, int.class, java.io.OutputStream.class)
                .invoke(bitmap, png, 100, stream)).booleanValue();
        }
    }

    private static String encode(String value) {
        return Base64.getEncoder().encodeToString(value.getBytes(StandardCharsets.UTF_8));
    }

    private static final class ActiveMedia {
        final Object controller;
        final Object metadata;
        final Object playbackState;
        final String packageName;

        ActiveMedia(Object controller, Object metadata, Object playbackState, String packageName) {
            this.controller = controller;
            this.metadata = metadata;
            this.playbackState = playbackState;
            this.packageName = packageName;
        }
    }

    private static void render(Object ai, File out, int size) throws Throwable {
        Class<?> aiClass = ai.getClass();
        int icon = aiClass.getField("icon").getInt(ai);
        if (icon == 0) throw new IllegalStateException("no-icon");
        String sourceDir = (String) aiClass.getField("sourceDir").get(ai);
        String[] splits = (String[]) aiClass.getField("splitSourceDirs").get(ai);
        String[] libs = (String[]) aiClass.getField("sharedLibraryFiles").get(ai);

        Class<?> amClass = Class.forName("android.content.res.AssetManager");
        Constructor<?> amCtor = amClass.getDeclaredConstructor();
        amCtor.setAccessible(true);
        Object am = amCtor.newInstance();
        Method addAssetPath = amClass.getMethod("addAssetPath", String.class);
        addAssetPath.invoke(am, sourceDir);
        if (splits != null) for (String s : splits) addAssetPath.invoke(am, s);
        if (libs != null) {
            Method addShared = null;
            try {
                addShared = amClass.getMethod("addAssetPathAsSharedLibrary", String.class);
            } catch (NoSuchMethodException ignored) {
            }
            for (String l : libs) {
                if (!l.endsWith(".apk")) continue;
                try {
                    (addShared != null ? addShared : addAssetPath).invoke(am, l);
                } catch (Throwable ignored) {
                }
            }
        }

        Class<?> dmClass = Class.forName("android.util.DisplayMetrics");
        Object dm = dmClass.getConstructor().newInstance();
        dmClass.getMethod("setToDefaults").invoke(dm);
        dmClass.getField("densityDpi").setInt(dm, DENSITY);
        dmClass.getField("density").setFloat(dm, DENSITY / 160f);
        dmClass.getField("scaledDensity").setFloat(dm, DENSITY / 160f);

        Class<?> cfgClass = Class.forName("android.content.res.Configuration");
        Object cfg = cfgClass.getConstructor().newInstance();
        cfgClass.getMethod("setToDefaults").invoke(cfg);
        cfgClass.getField("densityDpi").setInt(cfg, DENSITY);

        Class<?> resClass = Class.forName("android.content.res.Resources");
        Object res = resClass.getConstructor(amClass, dmClass, cfgClass).newInstance(am, dm, cfg);
        Class<?> themeClass = Class.forName("android.content.res.Resources$Theme");
        Object theme = resClass.getMethod("newTheme").invoke(res);

        Method getDrawable = resClass.getMethod("getDrawableForDensity", int.class, int.class, themeClass);
        Object drawable;
        try {
            drawable = getDrawable.invoke(res, icon, DENSITY, theme);
        } catch (Throwable t) {
            drawable = getDrawable.invoke(res, icon, DENSITY, null);
        }
        if (drawable == null) throw new IllegalStateException("null-drawable");

        Class<?> cfgEnum = Class.forName("android.graphics.Bitmap$Config");
        Class<?> bmpClass = Class.forName("android.graphics.Bitmap");
        @SuppressWarnings({"unchecked", "rawtypes"})
        Object argb = Enum.valueOf((Class) cfgEnum, "ARGB_8888");
        Object bmp = bmpClass.getMethod("createBitmap", int.class, int.class, cfgEnum).invoke(null, size, size, argb);
        Class<?> canvasClass = Class.forName("android.graphics.Canvas");
        Object canvas = canvasClass.getConstructor(bmpClass).newInstance(bmp);

        Class<?> drawableClass = Class.forName("android.graphics.drawable.Drawable");
        drawableClass.getMethod("setBounds", int.class, int.class, int.class, int.class).invoke(drawable, 0, 0, size, size);
        drawableClass.getMethod("draw", canvasClass).invoke(drawable, canvas);

        Class<?> fmtEnum = Class.forName("android.graphics.Bitmap$CompressFormat");
        @SuppressWarnings({"unchecked", "rawtypes"})
        Object png = Enum.valueOf((Class) fmtEnum, "PNG");
        FileOutputStream fos = new FileOutputStream(out);
        try {
            bmpClass.getMethod("compress", fmtEnum, int.class, java.io.OutputStream.class).invoke(bmp, png, 100, fos);
        } finally {
            fos.close();
        }
    }
}
