package com.mixtervee.camptransferremote;

import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

public class MainActivityV31 extends MainActivityV30 {
    private static final String OLD_BRAND = "CampTransfer";
    private static final String NEW_BRAND = "Turtle Transfer";

    private final Handler brandingHandler = new Handler(Looper.getMainLooper());
    private final Runnable brandingSweep = new Runnable() {
        @Override
        public void run() {
            applyBranding();
            brandingHandler.postDelayed(this, 400);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setTitle("Turtle Transfer Remote");
        applyBranding();
    }

    @Override
    protected void onResume() {
        super.onResume();
        brandingHandler.removeCallbacks(brandingSweep);
        brandingHandler.post(brandingSweep);
    }

    @Override
    protected void onPause() {
        brandingHandler.removeCallbacks(brandingSweep);
        super.onPause();
    }

    private void applyBranding() {
        View content = findViewById(android.R.id.content);
        if (content != null) replaceInView(content);
    }

    private void replaceInView(View view) {
        if (view instanceof TextView) {
            TextView textView = (TextView) view;
            CharSequence text = textView.getText();
            if (text != null && text.toString().contains(OLD_BRAND))
                textView.setText(text.toString().replace(OLD_BRAND, NEW_BRAND));

            CharSequence hint = textView.getHint();
            if (hint != null && hint.toString().contains(OLD_BRAND))
                textView.setHint(hint.toString().replace(OLD_BRAND, NEW_BRAND));
        }

        CharSequence description = view.getContentDescription();
        if (description != null && description.toString().contains(OLD_BRAND))
            view.setContentDescription(description.toString().replace(OLD_BRAND, NEW_BRAND));

        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++)
                replaceInView(group.getChildAt(i));
        }
    }
}
