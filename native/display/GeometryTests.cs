using System;
using System.Drawing;
using EdgeLinkNative;
class GeometryTests {
    static void Equal(Rectangle a, Rectangle b) { if (a != b) throw new Exception(a + " != " + b); }
    static void Main() {
        var p = new Rectangle(0,0,3840,2160);
        var e = new Rectangle(640,2160,2560,720);
        Equal(Geometry.Canvas(p,e),new Rectangle(0,0,3840,2880));
        e = new Rectangle(-320,-720,2560,720);
        var c = Geometry.Canvas(p,e);
        Equal(c,new Rectangle(-320,-720,4160,2880));
        Equal(Geometry.Relative(p,c),new Rectangle(320,720,3840,2160));
        Equal(Geometry.Relative(e,c),new Rectangle(0,0,2560,720));
        bool rejected=false;
        try { Geometry.Canvas(p,new Rectangle(0,0,2560,720)); } catch(ArgumentException) { rejected=true; }
        if(!rejected) throw new Exception("Overlap accepted");
        Console.WriteLine("Geometry tests passed");
    }
}
