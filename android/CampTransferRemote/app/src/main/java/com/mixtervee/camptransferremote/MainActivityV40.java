package com.mixtervee.camptransferremote;

import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

public class MainActivityV40 extends MainActivityV30 {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setTitle("Turtle Transfer Remote");
        View content = findViewById(android.R.id.content);
        if (content != null) rebrand(content);
    }

    private void rebrand(View view) {
        if (view instanceof TextView) {
            TextView textView = (TextView) view;
            CharSequence current = textView.getText();
            if (current != null) {
                String branded = current.toString()
                        .replace("CampTransfer Remote", "Turtle Transfer Remote")
                        .replace("CampTransfer", "Turtle Transfer");
                if (!branded.contentEquals(current))
                    textView.setText(branded);
            }
        }

        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++)
                rebrand(group.getChildAt(i));
        }
    }
}
