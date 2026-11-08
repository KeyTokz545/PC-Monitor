package com.keytokz.pcmonitor;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Petits éléments d'interface aux couleurs du tableau de bord (sans bibliothèque externe). */
final class Ui {
    static final int INK = Color.parseColor("#0E1822");
    static final int PANEL = Color.parseColor("#15212D");
    static final int LINE = Color.parseColor("#243444");
    static final int TEXT = Color.parseColor("#E4ECF2");
    static final int MUTED = Color.parseColor("#7F93A6");
    static final int COOL = Color.parseColor("#4FC3E0");
    static final int HOT = Color.parseColor("#FF5A4E");

    private Ui() {
    }

    static int dp(Context c, float v) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, c.getResources().getDisplayMetrics()));
    }

    static TextView text(Context c, String s, float sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return t;
    }

    static GradientDrawable box(Context c, int fill, int stroke) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(fill);
        d.setCornerRadius(dp(c, 16));
        if (stroke != 0) d.setStroke(dp(c, 1), stroke);
        return d;
    }

    /** Bouton plein (action principale) ou en contour (action secondaire). */
    static TextView button(Context c, String label, boolean primary) {
        TextView b = text(c, label, 16, primary ? INK : COOL, true);
        b.setGravity(Gravity.CENTER);
        b.setPadding(dp(c, 16), dp(c, 14), dp(c, 16), dp(c, 14));
        b.setBackground(box(c, primary ? COOL : PANEL, primary ? 0 : LINE));
        b.setClickable(true);
        b.setFocusable(true);
        return b;
    }

    static LinearLayout.LayoutParams full(Context c, int topMarginDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(c, topMarginDp);
        return lp;
    }
}
