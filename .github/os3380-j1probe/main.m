// Native UIKit probe for J1 (no .NET MAUI, no toolkit): what UIStatusBarManager.statusBarStyle reports for each style, per window
// user interface style, and what the screen shows. One binary, two bundles: Info.plist decides the mode.
//   vcbased  (no UIViewControllerBasedStatusBarAppearance key): the root view controller returns the style, as in the device-test runner.
//   appstyle (UIViewControllerBasedStatusBarAppearance=false): UIApplication setStatusBarStyle:animated: sets it, as StatusBar.ios.cs
//            TrySetApplicationStatusBarStyle does; READBACK is read synchronously right after the call, like the toolkit does.
// Built with -fno-objc-arc (short-lived process).
#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>
#import <objc/runtime.h>
#import <objc/message.h>
#import <dlfcn.h>

static CFTimeInterval t0;
static void Log(NSString *fmt, ...) NS_FORMAT_FUNCTION(1, 2);
static void Log(NSString *fmt, ...)
{
	va_list a; va_start(a, fmt);
	NSString *s = [[[NSString alloc] initWithFormat:fmt arguments:a] autorelease];
	va_end(a);
	fprintf(stdout, "[j1 %7.3f] %s\n", CACurrentMediaTime() - t0, s.UTF8String);
	fflush(stdout);
}

static NSString *SN(NSInteger s)
{
	switch (s)
	{
		case UIStatusBarStyleDefault: return @"Default";
		case UIStatusBarStyleLightContent: return @"LightContent";
		case UIStatusBarStyleDarkContent: return @"DarkContent";
		default: return [NSString stringWithFormat:@"%ld", (long)s];
	}
}

static NSString *UIS(UIUserInterfaceStyle s)
{
	switch (s)
	{
		case UIUserInterfaceStyleUnspecified: return @"Unspecified";
		case UIUserInterfaceStyleLight: return @"Light";
		case UIUserInterfaceStyleDark: return @"Dark";
		default: return [NSString stringWithFormat:@"%ld", (long)s];
	}
}

static UIWindowScene *gScene;
static UIWindow *gWindow;
static UIStatusBarStyle gPreferred = UIStatusBarStyleDefault;
static BOOL gAppStyleMode;

static NSString *Content(void)
{
	typedef UIImage *(*CreateFn)(void);
	CreateFn f = (CreateFn)dlsym(RTLD_DEFAULT, "_UICreateScreenUIImage");
	if (!f) return @"no _UICreateScreenUIImage";
	UIImage *img = f();
	CGImageRef cg = img.CGImage;
	size_t w = CGImageGetWidth(cg), h = CGImageGetHeight(cg);
	CGRect sb = gScene.statusBarManager.statusBarFrame;
	size_t strip = MIN(h, (size_t)ceil(CGRectGetMaxY(sb) * img.scale));
	if (strip == 0) { [img release]; return @"empty status bar"; }
	uint8_t *px = calloc(w * strip * 4, 1);
	CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
	CGContextRef ctx = CGBitmapContextCreate(px, w, strip, 8, w * 4, cs, (CGBitmapInfo)kCGImageAlphaPremultipliedLast);
	CGContextDrawImage(ctx, CGRectMake(0, (CGFloat)strip - (CGFloat)h, w, h), cg);
	long light = 0, dark = 0;
	for (size_t i = 0; i < w * strip * 4; i += 4)
	{
		int r = px[i], g = px[i + 1], b = px[i + 2];
		if (r > 220 && g > 220 && b > 220) light++;
		else if (r < 40 && g < 40 && b < 40) dark++;
	}
	CGContextRelease(ctx); CGColorSpaceRelease(cs); free(px); [img release];
	NSString *verdict = (light >= 500 && dark * 10 < light) ? @"WHITE" : (dark >= 500 && light * 10 < dark) ? @"BLACK" : @"MIXED";
	return [NSString stringWithFormat:@"%@ (%ld light, %ld dark px, top %zu rows of %zux%zu)", verdict, light, dark, strip, w, h];
}

// Called through the runtime: the iOS 27 SDK may no longer declare the deprecated UIApplication status bar style API.
static void SetApplicationStyle(UIStatusBarStyle style)
{
	((void (*)(id, SEL, NSInteger, BOOL))objc_msgSend)(UIApplication.sharedApplication, sel_registerName("setStatusBarStyle:animated:"), style, NO);
}

static NSInteger ApplicationStyle(void)
{
	return ((NSInteger (*)(id, SEL))objc_msgSend)(UIApplication.sharedApplication, sel_registerName("statusBarStyle"));
}

@interface RedVC : UIViewController
@end
@implementation RedVC
- (void)viewDidLoad { [super viewDidLoad]; self.view.backgroundColor = UIColor.redColor; }
- (UIStatusBarStyle)preferredStatusBarStyle { return gPreferred; }
@end

static NSString *State(void)
{
	UIViewController *vc = gWindow.rootViewController;
	return [NSString stringWithFormat:@"scene.statusBarStyle=%@ app.statusBarStyle=%@ vc.preferred=%@ window.override=%@ window.trait=%@ vc.trait=%@ screen.trait=%@",
		SN(gScene.statusBarManager.statusBarStyle), SN(ApplicationStyle()), SN(vc.preferredStatusBarStyle), UIS(gWindow.overrideUserInterfaceStyle),
		UIS(gWindow.traitCollection.userInterfaceStyle), UIS(vc.traitCollection.userInterfaceStyle), UIS(gScene.screen.traitCollection.userInterfaceStyle)];
}

static NSMutableArray *steps;
static void RunStep(NSUInteger i)
{
	if (i >= steps.count) { Log(@"DONE"); exit(0); }
	NSArray *s = steps[i];
	dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)([s[0] doubleValue] * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
		((void (^)(void))s[1])();
		RunStep(i + 1);
	});
}

static void Step(double delay, void (^b)(void)) { [steps addObject:@[@(delay), [[b copy] autorelease]]]; }

static void ApplyStyle(UIStatusBarStyle style)
{
	if (gAppStyleMode)
	{
		SetApplicationStyle(style);
		[gWindow.rootViewController setNeedsStatusBarAppearanceUpdate];
	}
	else
	{
		gPreferred = style;
		[gWindow.rootViewController setNeedsStatusBarAppearanceUpdate];
	}
}

static void BuildSteps(void)
{
	steps = [[NSMutableArray alloc] init];
	NSString *mode = gAppStyleMode ? @"appstyle (UIViewControllerBasedStatusBarAppearance=false)" : @"vcbased (key absent)";
	Step(1.5, ^{ Log(@"OS %@ (%@) mode=%@", UIDevice.currentDevice.systemVersion, UIDevice.currentDevice.model, mode); Log(@"START %@", State()); });

	UIUserInterfaceStyle overrides[] = { UIUserInterfaceStyleUnspecified, UIUserInterfaceStyleLight, UIUserInterfaceStyleDark, UIUserInterfaceStyleUnspecified };
	UIStatusBarStyle styles[] = { UIStatusBarStyleDefault, UIStatusBarStyleLightContent, UIStatusBarStyleDarkContent, UIStatusBarStyleDefault };
	for (int o = 0; o < 4; o++)
	{
		UIUserInterfaceStyle ui = overrides[o];
		Step(0, ^{ gWindow.overrideUserInterfaceStyle = ui; [gWindow.rootViewController setNeedsStatusBarAppearanceUpdate]; });
		for (int s = 0; s < 4; s++)
		{
			UIStatusBarStyle style = styles[s];
			NSString *label = [NSString stringWithFormat:@"override=%@ set=%@", UIS(ui), SN(style)];
			Step(0.6, ^{ ApplyStyle(style); Log(@"READBACK %@: %@", label, State()); });
			Step(0.8, ^{ Log(@"SETTLED  %@: %@ screen=%@", label, State(), Content()); });
		}
	}
}

@interface SceneDelegate : UIResponder <UIWindowSceneDelegate>
@property (strong, nonatomic) UIWindow *window;
@end
@implementation SceneDelegate
- (void)scene:(UIScene *)scene willConnectToSession:(UISceneSession *)session options:(UISceneConnectionOptions *)connectionOptions
{
	gScene = (UIWindowScene *)scene;
	gWindow = [[UIWindow alloc] initWithWindowScene:gScene];
	gWindow.rootViewController = [[[RedVC alloc] init] autorelease];
	[gWindow makeKeyAndVisible];
	self.window = gWindow;
	id key = [NSBundle.mainBundle objectForInfoDictionaryKey:@"UIViewControllerBasedStatusBarAppearance"];
	gAppStyleMode = [key isKindOfClass:NSNumber.class] && ![key boolValue];
	BuildSteps();
	RunStep(0);
}
@end

@interface AppDelegate : UIResponder <UIApplicationDelegate>
@end
@implementation AppDelegate
- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)launchOptions { return YES; }
@end

int main(int argc, char *argv[])
{
	t0 = CACurrentMediaTime();
	@autoreleasepool { return UIApplicationMain(argc, argv, nil, NSStringFromClass(AppDelegate.class)); }
}
